// ================================================
//描 述:
//作 者:AlanDu
//创建时间:2023-05-31 16-17-16
//修改作者:AlanDu
//修改时间:2023-05-31 16-17-16
//版 本:0.1 
// ===============================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GameFramework;
using GameFramework.Event;
using GameFramework.Resource;
using Main.Runtime;
using Main.Runtime.Procedure;
using UnityEngine;
using UnityEngine.Networking;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;
using ResourceUpdateChangedEventArgs = UnityGameFramework.Runtime.ResourceUpdateChangedEventArgs;
using ResourceUpdateFailureEventArgs = UnityGameFramework.Runtime.ResourceUpdateFailureEventArgs;
using ResourceUpdateStartEventArgs = UnityGameFramework.Runtime.ResourceUpdateStartEventArgs;
using ResourceUpdateSuccessEventArgs = UnityGameFramework.Runtime.ResourceUpdateSuccessEventArgs;
using Utility = GameFramework.Utility;

namespace HotfixBusiness.Procedure
{
    public class UpdateInfoData
    {
        private readonly string m_Name;

        public UpdateInfoData(string name)
        {
            m_Name = name;
        }

        public string Name => m_Name;

        public int Length
        {
            get;
            set;
        }
    }
    public class ProcedureCheckAssets : ProcedureBase
    {
        public override bool UseNativeDialog => false;
        private string m_NextProcedure;
        private long m_UpdateTotalZipLength = 0;
        private bool m_NoticeUpdate = false;
        private float m_LastUpdateTime = 0;
        private List<UpdateInfoData> m_UpdateInfoDatas = new List<UpdateInfoData>();
        private List<string> m_HotUpdateAsm;
        private Dictionary<string,byte[]> m_HotfixAssemblyBytes = new Dictionary<string, byte[]>();
        private List<Assembly> m_HotfixAssemblys = new List<Assembly>();
        private LoadAssetCallbacks m_LoadAssetCallbacks;
        private int m_LoadAssetCount;
        private int m_FailureAssetCount;
        private bool m_IsResetProcedure;

        private Dictionary<ResourcesType, UpdateResourceInfo> m_UpdateResourceInfos = new();

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            m_NextProcedure = procedureOwner.GetData<VarString>("nextProcedure");
            m_NoticeUpdate = false;
            
            m_UpdateResourceInfos.Clear();
            m_UpdateResourceInfos.Add(ResourcesType.Resources, new UpdateResourceInfo(ResourcesType.Resources, false, false,false,0,0));
            m_UpdateResourceInfos.Add(ResourcesType.Assemblies, new UpdateResourceInfo(ResourcesType.Assemblies, false, false,false,0,0));
            
            m_UpdateTotalZipLength = 0;
            m_IsResetProcedure = false;
            
            OnStartLoadAsset();
            GameEntry.Event.Subscribe(ResourceUpdateStartEventArgs.EventId, OnResourceUpdateStart);
            GameEntry.Event.Subscribe(ResourceUpdateChangedEventArgs.EventId, OnResourceUpdateChanged);
            GameEntry.Event.Subscribe(ResourceUpdateSuccessEventArgs.EventId, OnResourceUpdateSuccess);
            GameEntry.Event.Subscribe(ResourceUpdateFailureEventArgs.EventId, OnResourceUpdateFailure);
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            foreach (var item in m_UpdateResourceInfos)
            {
                if (!item.Value.UpdateComplete)
                {
                    return;
                }
            }
            GameEntry.UI.DeerUIInitRootForm().OnOpenLoadingForm(false);
            GameEntry.Setting.SetString("nextProcedure",m_NextProcedure);
            if (m_IsResetProcedure)
            {
                GameEntry.ResetProcedure(Constant.Procedure.ProcedureResetMain);
            }
            else
            {
                ChangeState<ProcedureResetMain>(m_ProcedureOwner);
            }
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            base.OnLeave(procedureOwner, isShutdown);
            GameEntry.Event.Unsubscribe(ResourceUpdateStartEventArgs.EventId, OnResourceUpdateStart);
            GameEntry.Event.Unsubscribe(ResourceUpdateChangedEventArgs.EventId, OnResourceUpdateChanged);
            GameEntry.Event.Unsubscribe(ResourceUpdateSuccessEventArgs.EventId, OnResourceUpdateSuccess);
            GameEntry.Event.Unsubscribe(ResourceUpdateFailureEventArgs.EventId, OnResourceUpdateFailure);
        }
        private UpdateResourceInfo GetUpdateResourceInfo(ResourcesType resourcesType)
        {
            try
            {
                m_UpdateResourceInfos.TryGetValue(resourcesType, out UpdateResourceInfo cvi);
                if (cvi != null) return cvi;
                else throw new Exception("Can not find the UpdateResourceInfo for the specified ResourcesType");
            }
            catch (Exception e)
            {
                Logger.Error(e.ToString());
                throw;
            }
        }
        private long GetUpdateTotalZipLength()
        {
            long len = 0;
            foreach (var item in m_UpdateResourceInfos)
            {
                len += item.Value.UpdateLength;
            }

            return len;
        }
        private void OnStartLoadAsset()
        {
            UnloadAllResources();
            string groupName = Constant.Procedure.FindAssetGroup(m_NextProcedure);
            m_HotUpdateAsm = DeerSettingsUtils.GetHotUpdateAssemblies(groupName);
            if (GameEntryMain.Base.EditorResourceMode)
            {
                UpdateResourceInfo updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Resources);
                if (updateResourceInfo != null)
                {
                    updateResourceInfo.NeedUpdate = false;
                    updateResourceInfo.UpdateComplete = true;
                }
                if (AddHotAssemblyToArray())
                {
                    updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Assemblies);
                    if (updateResourceInfo != null)
                    {
                        updateResourceInfo.NeedUpdate = false;
                        updateResourceInfo.UpdateComplete = true;
                    }
                }
            }
            else
            {
                bool isCheckAsset = Constant.Procedure.IsCheckAsset(m_NextProcedure);
                if (!isCheckAsset)
                {
                    OnNoticeUpdate();
                    return;
                }
                if (string.IsNullOrEmpty(groupName))
                {
                    OnNoticeUpdate();
                    return;
                }
                IResourceGroup resourceGroup = GameEntryMain.Resource.GetResourceGroup(groupName);
                if (resourceGroup == null)
                {
                    OnNoticeUpdate();
                    Logger.Error($"has no resource group {groupName}");
                    return;
                }
                bool isReady = resourceGroup.Ready;
                if (!isReady)
                {
                    UpdateResourceInfo updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Resources);
                    if (updateResourceInfo != null)
                    {
                        updateResourceInfo.NeedUpdate = true;
                        updateResourceInfo.UpdateLength =
                            resourceGroup.TotalCompressedLength - resourceGroup.ReadyCompressedLength;
                        updateResourceInfo.UpdateCount = resourceGroup.TotalCount - resourceGroup.ReadyCount;
                    }
                }
                GameEntryMain.Assemblies.CheckAssemblies(groupName, delegate(int count, long length)
                {
                    UpdateResourceInfo updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Assemblies);
                    if (updateResourceInfo != null)
                    {
                        updateResourceInfo.NeedUpdate = true;
                        updateResourceInfo.UpdateLength = length;   
                        updateResourceInfo.UpdateCount = count;
                    }
                    OnNoticeUpdate();
                });
            }
        }

        private bool IsAddAllFinish(string aAssembly =null)
        {
            bool isAllAddFinish = true;
            List<Assembly> _assemblies = GameEntry.GetHotfixAssemblys();
            if (aAssembly != null)
            {
                foreach (var assembly in _assemblies)
                {
                    if (aAssembly == $"{assembly.GetName().Name}.dll")
                    {
                        return true;
                    }
                }
                return false;
            }
            foreach (var hotUpdateAsm in m_HotUpdateAsm)
            {
                bool isFind = false;
                foreach (var assembly in _assemblies)
                {
                    if (hotUpdateAsm == $"{assembly.GetName().Name}.dll")
                    {
                        isFind = true;
                    }
                }
                if (!isFind)
                {
                    isAllAddFinish = false;
                    break;
                }
            }
            if (isAllAddFinish)
            {
                return true;
            }
            return false;
        }

        private bool AddHotAssemblyToArray()
        {
            if (m_HotUpdateAsm.Count == 0)
            {
                return true;
            }

            if (IsAddAllFinish())
            {
                return true;
            }
            m_HotfixAssemblys.Clear();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var hotUpdateDllName in m_HotUpdateAsm)
                {
                    if (hotUpdateDllName == $"{asm.GetName().Name}.dll")
                    {
                        m_HotfixAssemblys.Add(asm);
                        m_IsResetProcedure = true;
                        GameEntry.AddHotfixAssemblys(asm);
                    }
                }
                if (m_HotfixAssemblys.Count == m_HotUpdateAsm.Count)
                {

                    return true;
                }
            }
            return false;
        }

        private void OnNoticeUpdate()
        {
            if (m_NoticeUpdate)
            {
                return;
            }
            m_NoticeUpdate = true;
            m_UpdateTotalZipLength = GetUpdateTotalZipLength();
            /*if (m_UpdateTotalZipLength > 0)
            {
                string conetnt = Utility.Text.Format("有{0}更新", FileUtils.GetLengthString(m_UpdateTotalZipLength));
                UnityGameFramework.Runtime.Log.Info(conetnt);
                DialogParams dialogParams = ReferencePool.Acquire<DialogParams>();
                dialogParams.Mode = 2;
                dialogParams.Title = "提示";
                dialogParams.ConfirmText = "确定";
                dialogParams.CancelText = "取消";
                dialogParams.Message = Utility.Text.Format("更新文件大小{0}，建议你在WIFI环境下进行下载，是否现在更新？", FileUtils.GetLengthString(m_UpdateTotalZipLength));
                dialogParams.OnClickConfirm = (object o) => { StartUpdate(); };
                dialogParams.OnClickCancel = (object o) => { Application.Quit(); };
                GameEntryMain.UI.DeerUIInitRootForm().OnOpenUIDialogForm(dialogParams);
            }
            else
            {
                StartUpdate();
            }*/
            StartUpdate();
        }

        private void LoadHotUpdateAssembly()
        {
            if (Application.isEditor || !DeerSettingsUtils.DeerHybridCLRSettings.Enable)
            {
                if (AddHotAssemblyToArray())
                {
                    UpdateResourceInfo updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Assemblies);
                    if (updateResourceInfo != null)
                    {
                        updateResourceInfo.UpdateComplete = true;
                    }
                }
            }
            else
            {
                m_HotfixAssemblyBytes.Clear();
                foreach (var assemblyName in m_HotUpdateAsm)
                {
                    string fileLoadPath;
                    if (GameEntryMain.Resource.ResourceMode == ResourceMode.Package)
                    {
                        fileLoadPath = Path.Combine(Application.streamingAssetsPath,DeerSettingsUtils.DeerHybridCLRSettings.HybridCLRAssemblyPath,DeerSettingsUtils.HotfixNode);
                        AssemblyInfo assemblyInfo = GameEntryMain.Assemblies.FindAssemblyInfoByName(assemblyName);
                        if (assemblyInfo != null)
                        {
                            fileLoadPath = Utility.Path.GetRegularPath(Path.Combine(fileLoadPath,$"{assemblyInfo.Name}.{assemblyInfo.HashCode}{DeerSettingsUtils.DeerHybridCLRSettings.AssemblyAssetExtension}"));
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX || UNITY_IOS
                            fileLoadPath = $"file://{fileLoadPath}";
#endif
                        }
                        else
                        {
                            Logger.Error<ProcedureCheckAssets>("本地没有资源：" + $"{assemblyName}");
                            return;
                        }
                    }
                    else
                    {
                        fileLoadPath = Path.Combine(Application.persistentDataPath,DeerSettingsUtils.DeerHybridCLRSettings.HybridCLRAssemblyPath,DeerSettingsUtils.HotfixNode);
                        fileLoadPath = Utility.Path.GetRegularPath(Path.Combine(fileLoadPath,$"{assemblyName}{DeerSettingsUtils.DeerHybridCLRSettings.AssemblyAssetExtension}"));
                        if (!File.Exists(fileLoadPath))
                        {
                            Logger.Error<ProcedureCheckAssets>($"fileLoadPath:{fileLoadPath} is not find.");
                        }
                        fileLoadPath = $"file://{fileLoadPath}";
                    }
                    Logger.Debug<ProcedureCheckAssets>($"fileLoadPath: {fileLoadPath} ");
                    GameEntryMain.Download.StartCoroutine(StartLoadAssemblyAsset(fileLoadPath,assemblyName));
                }
            }
        }
        IEnumerator StartLoadAssemblyAsset(string filePath ,string asmName)
        {
            UnityWebRequest unityWebRequest = UnityWebRequest.Get(filePath);
            yield return unityWebRequest.SendWebRequest();
            if (unityWebRequest.isDone)
            {
                if (unityWebRequest.result == UnityWebRequest.Result.Success)
                {
                    m_HotfixAssemblyBytes.Add(asmName,unityWebRequest.downloadHandler.data);
                    if (!IsAddAllFinish(asmName))
                    {
                        Assembly asm = Assembly.Load(unityWebRequest.downloadHandler.data);
                        m_IsResetProcedure = true;
                        GameEntry.AddHotfixAssemblys(asm);
                    }
                    if (m_HotfixAssemblyBytes.Count == m_HotUpdateAsm.Count)
                    {
                        UpdateResourceInfo updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Assemblies);
                        if (updateResourceInfo != null)
                        {
                            updateResourceInfo.UpdateComplete = true;
                        }
                    }
                }
                else
                {
                    Logger.Error<ProcedureCheckAssets>($"filePath:{filePath} load error:{unityWebRequest.error}");
                }
            }
            unityWebRequest.Dispose();
        }
        private void StartUpdate()
        {
            string groupName = Constant.Procedure.FindAssetGroup(m_NextProcedure);
            GameEntry.UI.DeerUIInitRootForm().OnOpenLoadingForm(true);

            foreach (var item in m_UpdateResourceInfos)
            {
                UpdateResourceInfo updateResourceInfo = item.Value;
                if (!updateResourceInfo.NeedUpdate)
                {
                    updateResourceInfo.UpdateComplete = true;
                    continue;
                }
                if (item.Key == ResourcesType.Resources)
                {
                    GameEntry.Resource.UpdateResources(groupName, OnUpdateResourcesComplete);
                }else if (item.Key == ResourcesType.Assemblies)
                {
                    GameEntryMain.Assemblies.UpdateAssemblies(groupName,OnUpdateAssembliesComplete);
                }
            }
        }

        private void OnUpdateAssembliesComplete(string groupName,bool result)
        {
            if (result)
            {
                LoadHotUpdateAssembly();
            } 
            else
            {
                Logger.Error("更新资源失败");
            }
        }


        //这个是下载成功之后的监听
        private void OnUpdateResourcesComplete(IResourceGroup resourceGroup, bool result)
        {
            if (result)
            {
                UpdateResourceInfo updateResourceInfo = GetUpdateResourceInfo(ResourcesType.Resources);
                if (updateResourceInfo != null)
                {
                    updateResourceInfo.UpdateComplete = true;
                }
            }
            else
            {
                Logger.Error("更新资源失败");
            }
        }
        //弹出提示
        private void OpenDisplay(string content)
        {
            DialogParams dialogParams = ReferencePool.Acquire<DialogParams>();
            dialogParams.Mode = 1;
            dialogParams.Title = "";
            dialogParams.Message = content;
            dialogParams.ConfirmText = "确认";
            dialogParams.OnClickConfirm = (object o) =>
            {
                Logger.Debug<ProcedureCheckAssets>("目前失败没有逻辑");
                //Application.Quit();
            };
            GameEntryMain.UI.DeerUIInitRootForm().OnOpenUIDialogForm(dialogParams);
        }
        private void RefreshProgress()
        {
            long currentTotalUpdateLength = 0L;
            for (int i = 0; i < m_UpdateInfoDatas.Count; i++)
            {
                currentTotalUpdateLength += m_UpdateInfoDatas[i].Length;
            }
            if (Time.time - m_LastUpdateTime > 1f)
            {
                m_LastUpdateTime = Time.time;
                int needTime = 0;
                if (GameEntryMain.Download.CurrentSpeed > 0)
                {
                    needTime = (int)((m_UpdateTotalZipLength - currentTotalUpdateLength) / GameEntryMain.Download.CurrentSpeed);
                }

                TimeSpan ts = new TimeSpan(0, 0, needTime);
                string timeStr = ts.ToString(@"mm\:ss");
                var updateProgress = $"剩余时间 {timeStr}({FileUtils.GetLengthString((int)GameEntryMain.Download.CurrentSpeed)}/s)";
                Logger.Info(updateProgress);
            }
            //float progressTotal = (float)currentTotalUpdateLength / m_UpdateTotalZipLength;
/*            Log.Info($"更新成功数量:{m_UpdateSuccessCount} 总更新数量:{m_UpdateConfigCount + m_UpdateResourceCount} 资源数量:{m_UpdateResourceCount} Config数量:{m_UpdateConfigCount}");
            Log.Info($"当前下载:{FileUtils.GetByteLengthString(currentTotalUpdateLength)} 总下载:{FileUtils.GetByteLengthString(m_UpdateTotalZipLength)} 下载进度:{progressTotal}");
            Log.Info($"下载速度:{FileUtils.GetByteLengthString((int)GameEntryMain.Download.CurrentSpeed)}");*/
            var tips = $"{FileUtils.GetByteLengthString(currentTotalUpdateLength)}/{FileUtils.GetByteLengthString(m_UpdateTotalZipLength)}  当前下载速度每秒{FileUtils.GetByteLengthString((int)GameEntryMain.Download.CurrentSpeed)}";
            GameEntryMain.UI.DeerUIInitRootForm().OnRefreshLoadingProgress(currentTotalUpdateLength, m_UpdateTotalZipLength, tips);
            /*MessengerInfo messengerInfo = ReferencePool.Acquire<MessengerInfo>();
            messengerInfo.param1 = m_UpdateTotalZipLength;
            messengerInfo.param2 = currentTotalUpdateLength;
            messengerInfo.param3 = $"当前下载进度:{FileUtils.GetByteLengthString(currentTotalUpdateLength)}/{FileUtils.GetByteLengthString(m_UpdateTotalZipLength)}   下载速度:{FileUtils.GetByteLengthString((int)GameEntryMain.Download.CurrentSpeed)}/s";*/
            //GameEntry.Messenger.SendEvent(EventName.EVENT_CS_UI_REFRESH_LOADING_PROGRESS,messengerInfo);
        }
        private void OnUpdateCompleteOne(string name, int length, UpdateStateType type)
        {
            for (int i = 0; i < m_UpdateInfoDatas.Count; i++)
            {
                if (m_UpdateInfoDatas[i].Name == name)
                {
                    if (type == UpdateStateType.Failure)
                    {
                        Logger.Warning($"Update '{name}' is failure.");
                        m_UpdateInfoDatas.Remove(m_UpdateInfoDatas[i]);
                    }
                    else
                    {
                        if (type == UpdateStateType.Start)
                        {
                            Logger.Warning($"Update '{name}' is invalid.");
                        }

                        if (type == UpdateStateType.Success)
                        {
                            Logger.Warning($"Update '{name}' is success.");
                        }
                        m_UpdateInfoDatas[i].Length = length;
                    }
                    RefreshProgress();
                    return;
                }
            }
        }
        private void OnResourceUpdateStart(object sender, GameEventArgs e)
        {
            ResourceUpdateStartEventArgs ne = (ResourceUpdateStartEventArgs)e;
            OnUpdateCompleteOne(ne.Name, 0, UpdateStateType.Start);
            m_UpdateInfoDatas.Add(new UpdateInfoData(ne.Name));
        }
        private void OnResourceUpdateChanged(object sender, GameEventArgs e)
        {
            ResourceUpdateChangedEventArgs ne = (ResourceUpdateChangedEventArgs)e;
            OnUpdateCompleteOne(ne.Name, ne.CurrentLength, UpdateStateType.Change);
        }
        private void OnResourceUpdateSuccess(object sender, GameEventArgs e)
        {
            ResourceUpdateSuccessEventArgs ne = (ResourceUpdateSuccessEventArgs)e;
            OnUpdateCompleteOne(ne.Name, ne.Length, UpdateStateType.Success);
        }
        private void OnResourceUpdateFailure(object sender, GameEventArgs e)
        {
            ResourceUpdateFailureEventArgs ne = (ResourceUpdateFailureEventArgs)e;
            if (ne.RetryCount >= ne.TotalRetryCount)
            {
                UnityGameFramework.Runtime.Log.Error("Update resource '{0}' failure from '{1}' with error message '{2}', retry count '{3}'.", ne.Name, ne.DownloadUri, ne.ErrorMessage, ne.RetryCount.ToString());
                OpenDisplay("当前网络不可用，请检查是否连接可用wifi或移动网络");
                return;
            }
            else
            {
                UnityGameFramework.Runtime.Log.Info("Update resource '{0}' failure from '{1}' with error message '{2}', retry count '{3}'.", ne.Name, ne.DownloadUri, ne.ErrorMessage, ne.RetryCount.ToString());
            }
            OnUpdateCompleteOne(ne.Name, 0, UpdateStateType.Failure);
        }
    }
}
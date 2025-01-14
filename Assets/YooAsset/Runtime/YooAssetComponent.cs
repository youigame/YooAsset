﻿using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace YooAsset
{
	public class YooAssetComponent 
	{
		/// <summary>
		/// 运行模式
		/// </summary>
		public enum EPlayMode
		{
			/// <summary>
			/// 编辑器下的模拟模式
			/// 注意：在初始化的时候自动构建真机模拟环境。
			/// </summary>
			EditorSimulateMode,

			/// <summary>
			/// 离线运行模式
			/// </summary>
			OfflinePlayMode,

			/// <summary>
			/// 联机运行模式
			/// </summary>
			HostPlayMode,
		}

		private bool _isInitialize = false;
		private string _initializeError = string.Empty;
		private EOperationStatus _initializeStatus = EOperationStatus.None;
		private EPlayMode _playMode;
		private IBundleServices _bundleServices;
		private ILocationServices _locationServices;
		private EditorSimulateModeImpl _editorSimulateModeImpl;
		private OfflinePlayModeImpl _offlinePlayModeImpl;
		private HostPlayModeImpl _hostPlayModeImpl;


		/// <summary>
		/// 是否已经初始化
		/// </summary>
		public bool IsInitialized
		{
			get { return _isInitialize; }
		}

		/// <summary>
		/// 异步初始化
		/// </summary>
		public InitializationOperation InitializeAsync(InitializeParameters parameters)
		{
			if (parameters == null)
				throw new Exception($"YooAsset create parameters is null.");

#if !UNITY_EDITOR
			if (parameters is EditorSimulateModeParameters)
				throw new Exception($"Editor simulate mode only support unity editor.");
#endif

			// 鉴定运行模式
			if (parameters is EditorSimulateModeParameters)
				_playMode = EPlayMode.EditorSimulateMode;
			else if (parameters is OfflinePlayModeParameters)
				_playMode = EPlayMode.OfflinePlayMode;
			else if (parameters is HostPlayModeParameters)
				_playMode = EPlayMode.HostPlayMode;
			else
				throw new NotImplementedException();

			if (parameters.LocationServices == null)
				throw new Exception($"{nameof(IBundleServices)} is null.");

			if (_playMode == EPlayMode.OfflinePlayMode)
			{
				var playModeParameters = parameters as OfflinePlayModeParameters;
				if (string.IsNullOrEmpty(playModeParameters.BuildinPackageName))
					throw new Exception($"{nameof(playModeParameters.BuildinPackageName)} is empty.");
			}

			if (_playMode == EPlayMode.HostPlayMode)
			{
				var playModeParameters = parameters as HostPlayModeParameters;
				if (playModeParameters.QueryServices == null)
					throw new Exception($"{nameof(IQueryServices)} is null.");
			}

			if (_isInitialize)
			{
				throw new Exception("YooAsset is initialized yet.");
			}

			_locationServices = parameters.LocationServices;

			// 检测参数范围
			if (parameters.AssetLoadingMaxNumber < 1)
			{
				parameters.AssetLoadingMaxNumber = 1;
				YooLogger.Warning($"{nameof(parameters.AssetLoadingMaxNumber)} minimum value is 1");
			}
			if (parameters.OperationSystemMaxTimeSlice < 30)
			{
				parameters.OperationSystemMaxTimeSlice = 30;
				YooLogger.Warning($"{nameof(parameters.OperationSystemMaxTimeSlice)} minimum value is 30 milliseconds");
			}

			// 创建驱动器
			if (_isInitialize == false)
			{
				_isInitialize = true;
				UnityEngine.GameObject driverGo = new UnityEngine.GameObject("[YooAsset]");
				driverGo.AddComponent<YooAssetDriver>();
				UnityEngine.Object.DontDestroyOnLoad(driverGo);

#if DEBUG
				driverGo.AddComponent<RemoteDebuggerInRuntime>();
#endif
			}

			// 初始化异步系统
			OperationSystem.Initialize(parameters.OperationSystemMaxTimeSlice);

			// 初始化下载系统
			if (_playMode == EPlayMode.HostPlayMode)
			{
				var hostPlayModeParameters = parameters as HostPlayModeParameters;
				CacheSystem.Initialize(hostPlayModeParameters.VerifyLevel);
				DownloadSystem.Initialize(hostPlayModeParameters.BreakpointResumeFileSize);
			}
			else
			{
				CacheSystem.Initialize(EVerifyLevel.Low);
				DownloadSystem.Initialize(int.MaxValue);
			}

			// 初始化资源系统
			InitializationOperation initializeOperation;
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				_editorSimulateModeImpl = new EditorSimulateModeImpl();
				_bundleServices = _editorSimulateModeImpl;
				AssetSystem.Initialize(true, parameters.AssetLoadingMaxNumber, parameters.DecryptionServices, _bundleServices);
				var simulateModeParameters = parameters as EditorSimulateModeParameters;
				initializeOperation = _editorSimulateModeImpl.InitializeAsync(
					simulateModeParameters.LocationToLower,
					simulateModeParameters.SimulatePatchManifestPath);
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				_offlinePlayModeImpl = new OfflinePlayModeImpl();
				_bundleServices = _offlinePlayModeImpl;
				AssetSystem.Initialize(false, parameters.AssetLoadingMaxNumber, parameters.DecryptionServices, _bundleServices);
				var playModeParameters = parameters as OfflinePlayModeParameters;
				initializeOperation = _offlinePlayModeImpl.InitializeAsync(
					playModeParameters.LocationToLower,
					playModeParameters.BuildinPackageName);
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				_hostPlayModeImpl = new HostPlayModeImpl();
				_bundleServices = _hostPlayModeImpl;
				AssetSystem.Initialize(false, parameters.AssetLoadingMaxNumber, parameters.DecryptionServices, _bundleServices);
				var playModeParameters = parameters as HostPlayModeParameters;
				initializeOperation = _hostPlayModeImpl.InitializeAsync(
					playModeParameters.LocationToLower,
					playModeParameters.DefaultHostServer,
					playModeParameters.FallbackHostServer,
					playModeParameters.QueryServices);
			}
			else
			{
				throw new NotImplementedException();
			}

			// 监听初始化结果
			initializeOperation.Completed += InitializeOperation_Completed;
			return initializeOperation;
		}
		private void InitializeOperation_Completed(AsyncOperationBase op)
		{
			_initializeStatus = op.Status;
			_initializeError = op.Error;
		}

		/// <summary>
		/// 向网络端请求静态资源版本
		/// </summary>
		/// <param name="packageName">更新的资源包裹名称</param>
		/// <param name="timeout">超时时间（默认值：60秒）</param>
		public UpdateStaticVersionOperation UpdateStaticVersionAsync(string packageName, int timeout = 60)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				var operation = new EditorPlayModeUpdateStaticVersionOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				var operation = new OfflinePlayModeUpdateStaticVersionOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.UpdateStaticVersionAsync(packageName, timeout);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		/// <summary>
		/// 向网络端请求并更新补丁清单
		/// </summary>
		/// <param name="packageName">更新的资源包裹名称</param>
		/// <param name="packageCRC">更新的资源包裹版本</param>
		/// <param name="timeout">超时时间（默认值：60秒）</param>
		public UpdateManifestOperation UpdateManifestAsync(string packageName, string packageCRC, int timeout = 60)
		{
			DebugCheckInitialize();
			DebugCheckUpdateManifest();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				var operation = new EditorPlayModeUpdateManifestOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				var operation = new OfflinePlayModeUpdateManifestOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.UpdatePatchManifestAsync(packageName, packageCRC, timeout);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		/// <summary>
		/// 弱联网情况下加载补丁清单
		/// 注意：当指定版本内容验证失败后会返回失败。
		/// </summary>
		/// <param name="packageName">指定的资源包裹名称</param>
		/// <param name="packageCRC">指定的资源包裹版本</param>
		public UpdateManifestOperation WeaklyUpdateManifestAsync(string packageName, string packageCRC)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				var operation = new EditorPlayModeUpdateManifestOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				var operation = new OfflinePlayModeUpdateManifestOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.WeaklyUpdatePatchManifestAsync(packageName, packageCRC);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		/// <summary>
		/// 开启一个异步操作
		/// </summary>
		/// <param name="operation">异步操作对象</param>
		public void StartOperation(GameAsyncOperation operation)
		{
			OperationSystem.StartOperation(operation);
		}

		/// <summary>
		/// 资源回收（卸载引用计数为零的资源）
		/// </summary>
		public void UnloadUnusedAssets()
		{
			if (_isInitialize)
			{
				AssetSystem.Update();
				AssetSystem.UnloadUnusedAssets();
			}
		}

		/// <summary>
		/// 强制回收所有资源
		/// </summary>
		public void ForceUnloadAllAssets()
		{
			if (_isInitialize)
			{
				AssetSystem.ForceUnloadAllAssets();
			}
		}


		#region 资源信息
		/// <summary>
		/// 是否需要从远端更新下载
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		public bool IsNeedDownloadFromRemote(string location)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, null);
			if (assetInfo.IsInvalid)
				return false;

			BundleInfo bundleInfo = _bundleServices.GetBundleInfo(assetInfo);
			if (bundleInfo.LoadMode == BundleInfo.ELoadMode.LoadFromRemote)
				return true;
			else
				return false;
		}

		/// <summary>
		/// 是否需要从远端更新下载
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		public bool IsNeedDownloadFromRemote(AssetInfo assetInfo)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
			{
				YooLogger.Warning(assetInfo.Error);
				return false;
			}

			BundleInfo bundleInfo = _bundleServices.GetBundleInfo(assetInfo);
			if (bundleInfo.LoadMode == BundleInfo.ELoadMode.LoadFromRemote)
				return true;
			else
				return false;
		}

		/// <summary>
		/// 获取资源信息列表
		/// </summary>
		/// <param name="tag">资源标签</param>
		public AssetInfo[] GetAssetInfos(string tag)
		{
			DebugCheckInitialize();
			string[] tags = new string[] { tag };
			return _bundleServices.GetAssetInfos(tags);
		}

		/// <summary>
		/// 获取资源信息列表
		/// </summary>
		/// <param name="tags">资源标签列表</param>
		public AssetInfo[] GetAssetInfos(string[] tags)
		{
			DebugCheckInitialize();
			return _bundleServices.GetAssetInfos(tags);
		}

		/// <summary>
		/// 获取资源信息
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		public AssetInfo GetAssetInfo(string location)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, null);
			return assetInfo;
		}

		/// <summary>
		/// 获取资源路径
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		/// <returns>如果location地址无效，则返回空字符串</returns>
		public string GetAssetPath(string location)
		{
			DebugCheckInitialize();
			return _locationServices.ConvertLocationToAssetPath(location);
		}
		#endregion

		#region 原生文件
		/// <summary>
		/// 异步获取原生文件
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		/// <param name="copyPath">拷贝路径</param>
		public RawFileOperation GetRawFileAsync(string location, string copyPath = null)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, null);
			return GetRawFileInternal(assetInfo, copyPath);
		}

		/// <summary>
		/// 异步获取原生文件
		/// </summary>
		/// <param name="assetInfo">资源信息</param>
		/// <param name="copyPath">拷贝路径</param>
		public RawFileOperation GetRawFileAsync(AssetInfo assetInfo, string copyPath = null)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
				YooLogger.Warning(assetInfo.Error);
			return GetRawFileInternal(assetInfo, copyPath);
		}


		private RawFileOperation GetRawFileInternal(AssetInfo assetInfo, string copyPath)
		{
			if (assetInfo.IsInvalid)
			{
				RawFileOperation operation = new CompletedRawFileOperation(assetInfo.Error, copyPath);
				OperationSystem.StartOperation(operation);
				return operation;
			}

			BundleInfo bundleInfo = _bundleServices.GetBundleInfo(assetInfo);

#if UNITY_EDITOR
			if (bundleInfo.Bundle.IsRawFile == false)
			{
				string error = $"Cannot load asset bundle file using {nameof(GetRawFileAsync)} method !";
				YooLogger.Error(error);
				RawFileOperation operation = new CompletedRawFileOperation(error, copyPath);
				OperationSystem.StartOperation(operation);
				return operation;
			}
#endif

			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				RawFileOperation operation = new EditorPlayModeRawFileOperation(bundleInfo, copyPath);
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				RawFileOperation operation = new OfflinePlayModeRawFileOperation(bundleInfo, copyPath);
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				RawFileOperation operation = new HostPlayModeRawFileOperation(bundleInfo, copyPath);
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else
			{
				throw new NotImplementedException();
			}
		}
		#endregion

		#region 场景加载
		/// <summary>
		/// 异步加载场景
		/// </summary>
		/// <param name="location">场景的定位地址</param>
		/// <param name="sceneMode">场景加载模式</param>
		/// <param name="activateOnLoad">加载完毕时是否主动激活</param>
		/// <param name="priority">优先级</param>
		public SceneOperationHandle LoadSceneAsync(string location, LoadSceneMode sceneMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, null);
			var handle = AssetSystem.LoadSceneAsync(assetInfo, sceneMode, activateOnLoad, priority);
			return handle;
		}

		/// <summary>
		/// 异步加载场景
		/// </summary>
		/// <param name="assetInfo">场景的资源信息</param>
		/// <param name="sceneMode">场景加载模式</param>
		/// <param name="activateOnLoad">加载完毕时是否主动激活</param>
		/// <param name="priority">优先级</param>
		public SceneOperationHandle LoadSceneAsync(AssetInfo assetInfo, LoadSceneMode sceneMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
				YooLogger.Warning(assetInfo.Error);
			var handle = AssetSystem.LoadSceneAsync(assetInfo, sceneMode, activateOnLoad, priority);
			return handle;
		}
		#endregion

		#region 资源加载
		/// <summary>
		/// 同步加载资源对象
		/// </summary>
		/// <param name="assetInfo">资源信息</param>
		public AssetOperationHandle LoadAssetSync(AssetInfo assetInfo)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
				YooLogger.Warning(assetInfo.Error);
			return LoadAssetInternal(assetInfo, true);
		}

		/// <summary>
		/// 同步加载资源对象
		/// </summary>
		/// <typeparam name="TObject">资源类型</typeparam>
		/// <param name="location">资源的定位地址</param>
		public AssetOperationHandle LoadAssetSync<TObject>(string location) where TObject : UnityEngine.Object
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, typeof(TObject));
			return LoadAssetInternal(assetInfo, true);
		}

		/// <summary>
		/// 同步加载资源对象
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		/// <param name="type">资源类型</param>
		public AssetOperationHandle LoadAssetSync(string location, System.Type type)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, type);
			return LoadAssetInternal(assetInfo, true);
		}


		/// <summary>
		/// 异步加载资源对象
		/// </summary>
		/// <param name="assetInfo">资源信息</param>
		public AssetOperationHandle LoadAssetAsync(AssetInfo assetInfo)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
				YooLogger.Warning(assetInfo.Error);
			return LoadAssetInternal(assetInfo, false);
		}

		/// <summary>
		/// 异步加载资源对象
		/// </summary>
		/// <typeparam name="TObject">资源类型</typeparam>
		/// <param name="location">资源的定位地址</param>
		public AssetOperationHandle LoadAssetAsync<TObject>(string location) where TObject : UnityEngine.Object
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, typeof(TObject));
			return LoadAssetInternal(assetInfo, false);
		}

		/// <summary>
		/// 异步加载资源对象
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		/// <param name="type">资源类型</param>
		public AssetOperationHandle LoadAssetAsync(string location, System.Type type)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, type);
			return LoadAssetInternal(assetInfo, false);
		}


		private AssetOperationHandle LoadAssetInternal(AssetInfo assetInfo, bool waitForAsyncComplete)
		{
#if UNITY_EDITOR
			BundleInfo bundleInfo = _bundleServices.GetBundleInfo(assetInfo);
			if (bundleInfo.Bundle.IsRawFile)
			{
				string error = $"Cannot load raw file using LoadAsset method !";
				YooLogger.Error(error);
				CompletedProvider completedProvider = new CompletedProvider(assetInfo);
				completedProvider.SetCompleted(error);
				return completedProvider.CreateHandle<AssetOperationHandle>();
			}
#endif

			var handle = AssetSystem.LoadAssetAsync(assetInfo);
			if (waitForAsyncComplete)
				handle.WaitForAsyncComplete();
			return handle;
		}
		#endregion

		#region 资源加载
		/// <summary>
		/// 同步加载子资源对象
		/// </summary>
		/// <param name="assetInfo">资源信息</param>
		public SubAssetsOperationHandle LoadSubAssetsSync(AssetInfo assetInfo)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
				YooLogger.Warning(assetInfo.Error);
			return LoadSubAssetsInternal(assetInfo, true);
		}

		/// <summary>
		/// 同步加载子资源对象
		/// </summary>
		/// <typeparam name="TObject">资源类型</typeparam>
		/// <param name="location">资源的定位地址</param>
		public SubAssetsOperationHandle LoadSubAssetsSync<TObject>(string location) where TObject : UnityEngine.Object
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, typeof(TObject));
			return LoadSubAssetsInternal(assetInfo, true);
		}

		/// <summary>
		/// 同步加载子资源对象
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		/// <param name="type">子对象类型</param>
		public SubAssetsOperationHandle LoadSubAssetsSync(string location, System.Type type)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, type);
			return LoadSubAssetsInternal(assetInfo, true);
		}


		/// <summary>
		/// 异步加载子资源对象
		/// </summary>
		/// <param name="assetInfo">资源信息</param>
		public SubAssetsOperationHandle LoadSubAssetsAsync(AssetInfo assetInfo)
		{
			DebugCheckInitialize();
			if (assetInfo.IsInvalid)
				YooLogger.Warning(assetInfo.Error);
			return LoadSubAssetsInternal(assetInfo, false);
		}

		/// <summary>
		/// 异步加载子资源对象
		/// </summary>
		/// <typeparam name="TObject">资源类型</typeparam>
		/// <param name="location">资源的定位地址</param>
		public SubAssetsOperationHandle LoadSubAssetsAsync<TObject>(string location) where TObject : UnityEngine.Object
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, typeof(TObject));
			return LoadSubAssetsInternal(assetInfo, false);
		}

		/// <summary>
		/// 异步加载子资源对象
		/// </summary>
		/// <param name="location">资源的定位地址</param>
		/// <param name="type">子对象类型</param>
		public SubAssetsOperationHandle LoadSubAssetsAsync(string location, System.Type type)
		{
			DebugCheckInitialize();
			AssetInfo assetInfo = ConvertLocationToAssetInfo(location, type);
			return LoadSubAssetsInternal(assetInfo, false);
		}


		private SubAssetsOperationHandle LoadSubAssetsInternal(AssetInfo assetInfo, bool waitForAsyncComplete)
		{
#if UNITY_EDITOR
			BundleInfo bundleInfo = _bundleServices.GetBundleInfo(assetInfo);
			if (bundleInfo.Bundle.IsRawFile)
			{
				string error = $"Cannot load raw file using LoadSubAssets method !";
				YooLogger.Error(error);
				CompletedProvider completedProvider = new CompletedProvider(assetInfo);
				completedProvider.SetCompleted(error);
				return completedProvider.CreateHandle<SubAssetsOperationHandle>();
			}
#endif

			var handle = AssetSystem.LoadSubAssetsAsync(assetInfo);
			if (waitForAsyncComplete)
				handle.WaitForAsyncComplete();
			return handle;
		}
		#endregion

		#region 资源下载
		/// <summary>
		/// 创建补丁下载器，用于下载更新资源标签指定的资源包文件
		/// </summary>
		/// <param name="tag">资源标签</param>
		/// <param name="downloadingMaxNumber">同时下载的最大文件数</param>
		/// <param name="failedTryAgain">下载失败的重试次数</param>
		public PatchDownloaderOperation CreatePatchDownloader(string tag, int downloadingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			return CreatePatchDownloader(new string[] { tag }, downloadingMaxNumber, failedTryAgain);
		}

		/// <summary>
		/// 创建补丁下载器，用于下载更新资源标签指定的资源包文件
		/// </summary>
		/// <param name="tags">资源标签列表</param>
		/// <param name="downloadingMaxNumber">同时下载的最大文件数</param>
		/// <param name="failedTryAgain">下载失败的重试次数</param>
		public PatchDownloaderOperation CreatePatchDownloader(string[] tags, int downloadingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode || _playMode == EPlayMode.OfflinePlayMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchDownloaderOperation(downloadList, downloadingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.CreatePatchDownloaderByTags(tags, downloadingMaxNumber, failedTryAgain);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		/// <summary>
		/// 创建补丁下载器，用于下载更新当前资源版本所有的资源包文件
		/// </summary>
		/// <param name="downloadingMaxNumber">同时下载的最大文件数</param>
		/// <param name="failedTryAgain">下载失败的重试次数</param>
		public PatchDownloaderOperation CreatePatchDownloader(int downloadingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode || _playMode == EPlayMode.OfflinePlayMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchDownloaderOperation(downloadList, downloadingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.CreatePatchDownloaderByAll(downloadingMaxNumber, failedTryAgain);
			}
			else
			{
				throw new NotImplementedException();
			}
		}


		/// <summary>
		/// 创建补丁下载器，用于下载更新指定的资源列表依赖的资源包文件
		/// </summary>
		/// <param name="locations">资源定位列表</param>
		/// <param name="downloadingMaxNumber">同时下载的最大文件数</param>
		/// <param name="failedTryAgain">下载失败的重试次数</param>
		public PatchDownloaderOperation CreateBundleDownloader(string[] locations, int downloadingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode || _playMode == EPlayMode.OfflinePlayMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchDownloaderOperation(downloadList, downloadingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				List<AssetInfo> assetInfos = new List<AssetInfo>(locations.Length);
				foreach (var location in locations)
				{
					AssetInfo assetInfo = ConvertLocationToAssetInfo(location, null);
					assetInfos.Add(assetInfo);
				}
				return _hostPlayModeImpl.CreatePatchDownloaderByPaths(assetInfos.ToArray(), downloadingMaxNumber, failedTryAgain);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		/// <summary>
		/// 创建补丁下载器，用于下载更新指定的资源列表依赖的资源包文件
		/// </summary>
		/// <param name="assetInfos">资源信息列表</param>
		/// <param name="downloadingMaxNumber">同时下载的最大文件数</param>
		/// <param name="failedTryAgain">下载失败的重试次数</param>
		public PatchDownloaderOperation CreateBundleDownloader(AssetInfo[] assetInfos, int downloadingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode || _playMode == EPlayMode.OfflinePlayMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchDownloaderOperation(downloadList, downloadingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.CreatePatchDownloaderByPaths(assetInfos, downloadingMaxNumber, failedTryAgain);
			}
			else
			{
				throw new NotImplementedException();
			}
		}
		#endregion

		#region 资源解压
		/// <summary>
		/// 创建补丁解压器
		/// </summary>
		/// <param name="tag">资源标签</param>
		/// <param name="unpackingMaxNumber">同时解压的最大文件数</param>
		/// <param name="failedTryAgain">解压失败的重试次数</param>
		public PatchUnpackerOperation CreatePatchUnpacker(string tag, int unpackingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			return CreatePatchUnpacker(new string[] { tag }, unpackingMaxNumber, failedTryAgain);
		}

		/// <summary>
		/// 创建补丁解压器
		/// </summary>
		/// <param name="tags">资源标签列表</param>
		/// <param name="unpackingMaxNumber">同时解压的最大文件数</param>
		/// <param name="failedTryAgain">解压失败的重试次数</param>
		public PatchUnpackerOperation CreatePatchUnpacker(string[] tags, int unpackingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchUnpackerOperation(downloadList, unpackingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchUnpackerOperation(downloadList, unpackingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.CreatePatchUnpackerByTags(tags, unpackingMaxNumber, failedTryAgain);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		/// <summary>
		/// 创建补丁解压器
		/// </summary>
		/// <param name="unpackingMaxNumber">同时解压的最大文件数</param>
		/// <param name="failedTryAgain">解压失败的重试次数</param>
		public PatchUnpackerOperation CreatePatchUnpacker(int unpackingMaxNumber, int failedTryAgain)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchUnpackerOperation(downloadList, unpackingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				List<BundleInfo> downloadList = new List<BundleInfo>();
				var operation = new PatchUnpackerOperation(downloadList, unpackingMaxNumber, failedTryAgain);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.CreatePatchUnpackerByAll(unpackingMaxNumber, failedTryAgain);
			}
			else
			{
				throw new NotImplementedException();
			}
		}
		#endregion

		#region 包裹更新
		/// <summary>
		/// 创建资源包裹下载器，用于下载更新指定资源版本所有的资源包文件
		/// </summary>
		/// <param name="packageName">指定更新的资源包裹名称</param>
		/// <param name="packageCRC">指定更新的资源包裹版本</param>
		/// <param name="timeout">超时时间</param>
		public UpdatePackageOperation UpdatePackageAsync(string packageName, string packageCRC, int timeout = 60)
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				var operation = new EditorPlayModeUpdatePackageOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				var operation = new OfflinePlayModeUpdatePackageOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				return _hostPlayModeImpl.UpdatePackageAsync(packageName, packageCRC, timeout);
			}
			else
			{
				throw new NotImplementedException();
			}
		}
		#endregion

		#region 沙盒相关
		/// <summary>
		/// 获取沙盒的根路径
		/// </summary>
		public string GetSandboxRoot()
		{
			return PathHelper.MakePersistentRootPath();
		}

		/// <summary>
		/// 清空沙盒目录
		/// </summary>
		public void ClearSandbox()
		{
			SandboxHelper.DeleteSandbox();
		}

		/// <summary>
		/// 清空所有的缓存文件
		/// </summary>
		public void ClearAllCacheFiles()
		{
			SandboxHelper.DeleteCacheFolder();
		}

		/// <summary>
		/// 清空未被使用的缓存文件
		/// </summary>
		public ClearUnusedCacheFilesOperation ClearUnusedCacheFiles()
		{
			DebugCheckInitialize();
			if (_playMode == EPlayMode.EditorSimulateMode)
			{
				var operation = new EditorPlayModeClearUnusedCacheFilesOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.OfflinePlayMode)
			{
				var operation = new OfflinePlayModeClearUnusedCacheFilesOperation();
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else if (_playMode == EPlayMode.HostPlayMode)
			{
				var operation = new HostPlayModeClearUnusedCacheFilesOperation(_hostPlayModeImpl);
				OperationSystem.StartOperation(operation);
				return operation;
			}
			else
			{
				throw new NotImplementedException();
			}
		}
		#endregion

		#region 内部方法
		internal void InternalDestroy()
		{
			if (_isInitialize)
			{
				_isInitialize = false;
				_initializeError = string.Empty;
				_initializeStatus = EOperationStatus.None;

				_bundleServices = null;
				_locationServices = null;
				_editorSimulateModeImpl = null;
				_offlinePlayModeImpl = null;
				_hostPlayModeImpl = null;

				OperationSystem.DestroyAll();
				DownloadSystem.DestroyAll();
				CacheSystem.DestroyAll();
				AssetSystem.DestroyAll();
				YooLogger.Log("YooAssets destroy all !");
			}
		}
		internal void InternalUpdate()
		{
			OperationSystem.Update();
			DownloadSystem.Update();
			AssetSystem.Update();
		}

		/// <summary>
		/// 资源定位地址转换为资源完整路径
		/// </summary>
		internal string MappingToAssetPath(string location)
		{
			return _bundleServices.MappingToAssetPath(location);
		}
		#endregion

		#region 调试方法
		[Conditional("DEBUG")]
		private void DebugCheckInitialize()
		{
			if (_initializeStatus == EOperationStatus.None)
				throw new Exception("YooAssets initialize not completed !");
			else if (_initializeStatus == EOperationStatus.Failed)
				throw new Exception($"YooAssets initialize failed : {_initializeError}");
		}

		[Conditional("DEBUG")]
		private void DebugCheckLocation(string location)
		{
			if (string.IsNullOrEmpty(location) == false)
			{
				// 检查路径末尾是否有空格
				int index = location.LastIndexOf(" ");
				if (index != -1)
				{
					if (location.Length == index + 1)
						YooLogger.Warning($"Found blank character in location : \"{location}\"");
				}

				if (location.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
					YooLogger.Warning($"Found illegal character in location : \"{location}\"");
			}
		}

		[Conditional("DEBUG")]
		private void DebugCheckUpdateManifest()
		{
			var loadedBundleInfos = AssetSystem.GetLoadedBundleInfos();
			if (loadedBundleInfos.Count > 0)
			{
				YooLogger.Warning($"Found loaded bundle before update manifest ! Recommended to call the  {nameof(ForceUnloadAllAssets)} method to release loaded bundle !");
			}
		}
		#endregion

		#region 私有方法
		/// <summary>
		/// 资源定位地址转换为资源信息类，失败时内部会发出错误日志。
		/// </summary>
		/// <returns>如果转换失败会返回一个无效的资源信息类</returns>
		private AssetInfo ConvertLocationToAssetInfo(string location, System.Type assetType)
		{
			DebugCheckLocation(location);
			string assetPath = _locationServices.ConvertLocationToAssetPath(location);
			PatchAsset patchAsset = _bundleServices.TryGetPatchAsset(assetPath);
			if (patchAsset != null)
			{
				AssetInfo assetInfo = new AssetInfo(patchAsset, assetType);
				return assetInfo;
			}
			else
			{
				string error;
				if (string.IsNullOrEmpty(location))
					error = $"The location is null or empty !";
				else
					error = $"The location is invalid : {location}";
				YooLogger.Error(error);
				AssetInfo assetInfo = new AssetInfo(error);
				return assetInfo;
			}
		}
		#endregion
	}
}
﻿using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace YooAsset.Editor
{
	[TaskAttribute("创建构建报告文件")]
	public class TaskCreateReport : IBuildTask
	{
		void IBuildTask.Run(BuildContext context)
		{
			var buildParameters = context.GetContextObject<BuildParametersContext>();
			var buildMapContext = context.GetContextObject<BuildMapContext>();
			buildParameters.StopWatch();

			var buildMode = buildParameters.Parameters.BuildMode;
			if (buildMode != EBuildMode.SimulateBuild)
			{
				CreateReportFile(buildParameters, buildMapContext);
			}

			float buildSeconds = buildParameters.GetBuildingSeconds();
			BuildRunner.Info($"Build time consuming {buildSeconds} seconds.");
		}

		private void CreateReportFile(BuildParametersContext buildParametersContext, BuildMapContext buildMapContext)
		{
			var buildParameters = buildParametersContext.Parameters;

			string pipelineOutputDirectory = buildParametersContext.GetPipelineOutputDirectory();
			PatchManifest patchManifest = AssetBundleBuilderHelper.LoadPatchManifestFile(pipelineOutputDirectory, buildParameters.BuildPackage, buildParametersContext.OutputPackageCRC);
			BuildReport buildReport = new BuildReport();

			// 概述信息
			{
#if UNITY_2019_4_OR_NEWER
				UnityEditor.PackageManager.PackageInfo packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BuildReport).Assembly);
				if (packageInfo != null)
					buildReport.Summary.YooVersion = packageInfo.version;
#endif
				buildReport.Summary.UnityVersion = UnityEngine.Application.unityVersion;
				buildReport.Summary.BuildDate = DateTime.Now.ToString();
				buildReport.Summary.BuildSeconds = (int)buildParametersContext.GetBuildingSeconds();
				buildReport.Summary.BuildTarget = buildParameters.BuildTarget;
				buildReport.Summary.BuildPipeline = buildParameters.BuildPipeline;
				buildReport.Summary.BuildMode = buildParameters.BuildMode;
				buildReport.Summary.BuildPackage = buildParameters.BuildPackage;
				buildReport.Summary.EnableAddressable = buildParameters.EnableAddressable;
				buildReport.Summary.EncryptionServicesClassName = buildParameters.EncryptionServices == null ?
					"null" : buildParameters.EncryptionServices.GetType().FullName;

				// 构建参数
				buildReport.Summary.OutputNameStyle = buildParameters.OutputNameStyle;
				buildReport.Summary.CompressOption = buildParameters.CompressOption;
				buildReport.Summary.DisableWriteTypeTree = buildParameters.DisableWriteTypeTree;
				buildReport.Summary.IgnoreTypeTreeChanges = buildParameters.IgnoreTypeTreeChanges;

				// 构建结果
				buildReport.Summary.AssetFileTotalCount = buildMapContext.AssetFileCount;
				buildReport.Summary.MainAssetTotalCount = GetMainAssetCount(patchManifest);
				buildReport.Summary.AllBundleTotalCount = GetAllBundleCount(patchManifest);
				buildReport.Summary.AllBundleTotalSize = GetAllBundleSize(patchManifest);
				buildReport.Summary.EncryptedBundleTotalCount = GetEncryptedBundleCount(patchManifest);
				buildReport.Summary.EncryptedBundleTotalSize = GetEncryptedBundleSize(patchManifest);
				buildReport.Summary.RawBundleTotalCount = GetRawBundleCount(patchManifest);
				buildReport.Summary.RawBundleTotalSize = GetRawBundleSize(patchManifest);
			}

			// 资源对象列表
			buildReport.AssetInfos = new List<ReportAssetInfo>(patchManifest.AssetList.Count);
			foreach (var patchAsset in patchManifest.AssetList)
			{
				var mainBundle = patchManifest.BundleList[patchAsset.BundleID];
				ReportAssetInfo reportAssetInfo = new ReportAssetInfo();
				reportAssetInfo.Address = patchAsset.Address;
				reportAssetInfo.AssetPath = patchAsset.AssetPath;
				reportAssetInfo.AssetTags = patchAsset.AssetTags;
				reportAssetInfo.AssetGUID = AssetDatabase.AssetPathToGUID(patchAsset.AssetPath);
				reportAssetInfo.MainBundleName = mainBundle.BundleName;
				reportAssetInfo.MainBundleSize = mainBundle.FileSize;
				reportAssetInfo.DependBundles = GetDependBundles(patchManifest, patchAsset);
				reportAssetInfo.DependAssets = GetDependAssets(buildMapContext, mainBundle.BundleName, patchAsset.AssetPath);
				buildReport.AssetInfos.Add(reportAssetInfo);
			}

			// 资源包列表
			buildReport.BundleInfos = new List<ReportBundleInfo>(patchManifest.BundleList.Count);
			foreach (var patchBundle in patchManifest.BundleList)
			{
				ReportBundleInfo reportBundleInfo = new ReportBundleInfo();
				reportBundleInfo.BundleName = patchBundle.BundleName;
				reportBundleInfo.FileName = patchBundle.FileName;
				reportBundleInfo.FileHash = patchBundle.FileHash;
				reportBundleInfo.FileCRC = patchBundle.FileCRC;
				reportBundleInfo.FileSize = patchBundle.FileSize;
				reportBundleInfo.Tags = patchBundle.Tags;
				reportBundleInfo.Flags = patchBundle.Flags;
				buildReport.BundleInfos.Add(reportBundleInfo);
			}

			// 删除旧文件
			string fileName = YooAssetSettingsData.GetReportFileName(buildParameters.BuildPackage, buildParametersContext.OutputPackageCRC);
			string filePath = $"{pipelineOutputDirectory}/{fileName}";
			if (File.Exists(filePath))
				File.Delete(filePath);

			// 序列化文件
			BuildReport.Serialize(filePath, buildReport);
			BuildRunner.Log($"资源构建报告文件创建完成：{filePath}");
		}

		/// <summary>
		/// 获取资源对象依赖的所有资源包
		/// </summary>
		private List<string> GetDependBundles(PatchManifest patchManifest, PatchAsset patchAsset)
		{
			List<string> dependBundles = new List<string>(patchAsset.DependIDs.Length);
			foreach (int index in patchAsset.DependIDs)
			{
				string dependBundleName = patchManifest.BundleList[index].BundleName;
				dependBundles.Add(dependBundleName);
			}
			return dependBundles;
		}

		/// <summary>
		/// 获取资源对象依赖的其它所有资源
		/// </summary>
		private List<string> GetDependAssets(BuildMapContext buildMapContext, string bundleName, string assetPath)
		{
			List<string> result = new List<string>();
			if (buildMapContext.TryGetBundleInfo(bundleName, out BuildBundleInfo bundleInfo))
			{
				BuildAssetInfo findAssetInfo = null;
				foreach (var buildinAsset in bundleInfo.BuildinAssets)
				{
					if (buildinAsset.AssetPath == assetPath)
					{
						findAssetInfo = buildinAsset;
						break;
					}
				}
				if (findAssetInfo == null)
				{
					throw new Exception($"Not found asset {assetPath} in bunlde {bundleName}");
				}
				foreach (var dependAssetInfo in findAssetInfo.AllDependAssetInfos)
				{
					result.Add(dependAssetInfo.AssetPath);
				}
			}
			else
			{
				throw new Exception($"Not found bundle : {bundleName}");
			}
			return result;
		}

		private int GetMainAssetCount(PatchManifest patchManifest)
		{
			return patchManifest.AssetList.Count;
		}
		private int GetAllBundleCount(PatchManifest patchManifest)
		{
			return patchManifest.BundleList.Count;
		}
		private long GetAllBundleSize(PatchManifest patchManifest)
		{
			long fileBytes = 0;
			foreach (var patchBundle in patchManifest.BundleList)
			{
				fileBytes += patchBundle.FileSize;
			}
			return fileBytes;
		}
		private int GetEncryptedBundleCount(PatchManifest patchManifest)
		{
			int fileCount = 0;
			foreach (var patchBundle in patchManifest.BundleList)
			{
				if (patchBundle.IsEncrypted)
					fileCount++;
			}
			return fileCount;
		}
		private long GetEncryptedBundleSize(PatchManifest patchManifest)
		{
			long fileBytes = 0;
			foreach (var patchBundle in patchManifest.BundleList)
			{
				if (patchBundle.IsEncrypted)
					fileBytes += patchBundle.FileSize;
			}
			return fileBytes;
		}
		private int GetRawBundleCount(PatchManifest patchManifest)
		{
			int fileCount = 0;
			foreach (var patchBundle in patchManifest.BundleList)
			{
				if (patchBundle.IsRawFile)
					fileCount++;
			}
			return fileCount;
		}
		private long GetRawBundleSize(PatchManifest patchManifest)
		{
			long fileBytes = 0;
			foreach (var patchBundle in patchManifest.BundleList)
			{
				if (patchBundle.IsRawFile)
					fileBytes += patchBundle.FileSize;
			}
			return fileBytes;
		}
	}
}
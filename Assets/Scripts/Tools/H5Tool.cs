using System;
using System.Collections.Generic;
using UnityEngine;
using HDF.PInvoke;
using System.Text;
using System.Runtime.InteropServices;

public class H5Tool
{
    #region 路径处理

    private static string GetH5FullPath(string h5RelativePath)
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, h5RelativePath);
        return path.Replace("\\", "/");
    }

    #endregion

    #region 基础读取方法 (Int, Float, Double)

    public static int[] ReadInt32Array(string h5Path, string datasetPath)
    {
        return ReadDataset<int>(h5Path, datasetPath, H5T.NATIVE_INT32);
    }

    public static long[] ReadInt64Array(string h5Path, string datasetPath)
    {
        return ReadDataset<long>(h5Path, datasetPath, H5T.NATIVE_INT64);
    }

    public static float[] ReadFloatArray(string h5Path, string datasetPath)
    {
        return ReadDataset<float>(h5Path, datasetPath, H5T.NATIVE_FLOAT);
    }

    public static double[] ReadDoubleArray(string h5Path, string datasetPath)
    {
        return ReadDataset<double>(h5Path, datasetPath, H5T.NATIVE_DOUBLE);
    }

    /// <summary>
    /// 通用数据集读取逻辑
    /// </summary>
    private static T[] ReadDataset<T>(string h5Path, string datasetPath, long nativeType) where T : struct
    {
        string fullPath = GetH5FullPath(h5Path);
        long fileId = H5F.open(fullPath, H5F.ACC_RDONLY);
        if (fileId < 0) return null;

        long datasetId = H5D.open(fileId, datasetPath);
        if (datasetId < 0)
        {
            H5F.close(fileId);
            return null;
        }

        long spaceId = H5D.get_space(datasetId);
        long count = H5S.get_simple_extent_npoints(spaceId);

        T[] data = new T[count];
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);

        int status = H5D.read(datasetId, nativeType, H5S.ALL, H5S.ALL, H5P.DEFAULT, handle.AddrOfPinnedObject());

        handle.Free();
        H5S.close(spaceId);
        H5D.close(datasetId);
        H5F.close(fileId);

        return status >= 0 ? data : null;
    }

    #endregion

    #region Vector3 读取

    public static Vector3[] ReadVector3Array_Float64(string h5Path, string datasetPath = "/Vertexes")
    {
        double[] raw = ReadDoubleArray(h5Path, datasetPath);
        if (raw == null || raw.Length % 3 != 0) return null;

        Vector3[] res = new Vector3[raw.Length / 3];
        for (int i = 0; i < res.Length; i++)
        {
            res[i] = new Vector3((float)raw[i * 3], (float)raw[i * 3 + 1], (float)raw[i * 3 + 2]);
        }

        return res;
    }
    
    public static Vector3[] ReadVector3Array(string h5Path, string datasetPath)
    {
        double[] raw = ReadDoubleArray(h5Path, datasetPath);
        if (raw == null || raw.Length % 3 != 0) return null;

        int count = raw.Length / 3;
        Vector3[] result = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            result[i] = new Vector3(
                (float)raw[i * 3 + 0],
                (float)raw[i * 3 + 1],
                (float)raw[i * 3 + 2]
            );
        }

        return result;
    }

    #endregion

    #region 属性获取 (Attribute)

    public static string GetAttributeValue(string h5Path, string objectPath, string attrName)
    {
        string fullPath = GetH5FullPath(h5Path);
        // H5F.ACC_RDONLY 是只读打开
        long fileId = H5F.open(fullPath, H5F.ACC_RDONLY);
        if (fileId < 0) return null;

        // 打开对象（Group 或 Dataset）
        long objId = H5O.open(fileId, objectPath);
        if (objId < 0)
        {
            H5F.close(fileId);
            return null;
        }

        // 打开属性
        long attrId = H5A.open(objId, attrName);
        if (attrId < 0)
        {
            H5O.close(objId);
            H5F.close(fileId);
            return null;
        }

        // 获取属性的数据类型
        long fileTypeId = H5A.get_type(attrId);
        H5T.class_t typeClass = H5T.get_class(fileTypeId);
        string result = string.Empty;

        try
        {
            if (typeClass == H5T.class_t.STRING)
            {
                // 检查是否为变长字符串
                bool isVariable = H5T.is_variable_str(fileTypeId) > 0;
                if (isVariable)
                {
                    // 变长字符串读取逻辑
                    IntPtr[] ptrArray = new IntPtr[1];
                    GCHandle h = GCHandle.Alloc(ptrArray, GCHandleType.Pinned);
                    // 必须使用 H5T.C_S1 (C风格字符串) 作为内存类型
                    H5A.read(attrId, fileTypeId, h.AddrOfPinnedObject());
                    result = Marshal.PtrToStringAnsi(ptrArray[0]);
                    // 释放 HDF5 分配的内存
                    H5.free_memory(ptrArray[0]);
                    h.Free();
                }
                else
                {
                    // 固定长度字符串读取逻辑
                    long size = (long)H5T.get_size(fileTypeId);
                    byte[] buffer = new byte[size];
                    GCHandle h = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    H5A.read(attrId, fileTypeId, h.AddrOfPinnedObject());
                    result = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                    h.Free();
                }
            }
            else if (typeClass == H5T.class_t.FLOAT || typeClass == H5T.class_t.INTEGER)
            {
                // 数值读取：强制转换为 double 读取以保证兼容性
                double[] val = new double[1];
                GCHandle h = GCHandle.Alloc(val, GCHandleType.Pinned);
                H5A.read(attrId, H5T.NATIVE_DOUBLE, h.AddrOfPinnedObject());
                result = val[0].ToString("G"); // "G" 格式自动处理整数和浮点
                h.Free();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"读取属性 {attrName} 异常: {e.Message}");
        }
        finally
        {
            // 严格关闭所有 ID，防止文件占用
            H5T.close(fileTypeId);
            H5A.close(attrId);
            H5O.close(objId);
            H5F.close(fileId);
        }

        return result;
    }

    /// <summary>
    /// 检查属性是否存在
    /// </summary>
    public static bool AttributeExists(string h5Path, string objectPath, string attrName)
    {
        string fullPath = GetH5FullPath(h5Path);
        long fileId = H5F.open(fullPath, H5F.ACC_RDONLY);
        if (fileId < 0) return false;

        int exists = H5A.exists(fileId, objectPath + "/" + attrName); // 部分版本可能需打开Obj检查
        // 安全做法：
        long objId = H5O.open(fileId, objectPath);
        bool hasAttr = H5A.exists(objId, attrName) > 0;

        H5O.close(objId);
        H5F.close(fileId);
        return hasAttr;
    }

    #endregion

    #region 获取节点

    /// <summary>
    /// 获取指定路径下的所有子节点名称（包括组和数据集）
    /// </summary>
    /// <param name="h5Path">H5文件相对路径</param>
    /// <param name="groupPath">要查询的组路径，如 "/" 或 "/CellData"</param>
    /// <returns>子节点名称列表</returns>
    public static List<string> GetNodes(string h5Path, string groupPath)
    {
        List<string> nodeNames = new List<string>();
        string fullPath = GetH5FullPath(h5Path);

        long fileId = H5F.open(fullPath, H5F.ACC_RDONLY);
        if (fileId < 0) return nodeNames;

        long groupId = H5G.open(fileId, groupPath);
        if (groupId < 0)
        {
            H5F.close(fileId);
            return nodeNames;
        }

        try
        {
            H5G.info_t groupInfo = new H5G.info_t();
            if (H5G.get_info(groupId, ref groupInfo) < 0) return nodeNames;

            for (ulong i = 0; i < groupInfo.nlinks; i++)
            {
                // 1. 第一次调用：获取名称长度
                // 注意：在你的定义中，group_name 传 "." 代表当前 groupId 所在的路径
                // 第五个参数传 null 或空的 StringBuilder，size 传 0
                IntPtr sizeRet = H5L.get_name_by_idx(
                    groupId, 
                    ".", 
                    H5.index_t.NAME, 
                    H5.iter_order_t.INC, 
                    i, 
                    (StringBuilder)null, 
                    IntPtr.Zero
                );

                long nameSize = sizeRet.ToInt64();

                if (nameSize > 0)
                {
                    // 2. 第二次调用：正式读取
                    // 为 StringBuilder 分配空间（n + 1 用于结尾的 \0）
                    StringBuilder sb = new StringBuilder((int)nameSize + 1);
                    H5L.get_name_by_idx(
                        groupId, 
                        ".", 
                        H5.index_t.NAME, 
                        H5.iter_order_t.INC, 
                        i, 
                        sb, 
                        new IntPtr(sb.Capacity)
                    );

                    string nodeName = sb.ToString();
                    if (!string.IsNullOrEmpty(nodeName))
                    {
                        nodeNames.Add(nodeName);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"遍历 H5 节点失败: {e.Message}");
        }
        finally
        {
            H5G.close(groupId);
            H5F.close(fileId);
        }

        return nodeNames;
    }

    /// <summary>
    /// 获取子节点并区分类型（Group 或 Dataset）
    /// </summary>
    public static Dictionary<string, bool> GetNodesWithType(string h5Path, string groupPath)
    {
        var result = new Dictionary<string, bool>(); // Key: 名称, Value: 是否为 Group
        List<string> names = GetNodes(h5Path, groupPath);

        long fileId = H5F.open(GetH5FullPath(h5Path), H5F.ACC_RDONLY);
        if (fileId < 0) return result;

        foreach (var name in names)
        {
            string fullNodePath = groupPath.EndsWith("/") ? groupPath + name : groupPath + "/" + name;
            
            // 使用 H5O.get_info 来判断对象类型
            H5O.info_t info = new H5O.info_t();
            if (H5O.get_info_by_name(fileId, fullNodePath, ref info) >= 0)
            {
                result[name] = (info.type == H5O.type_t.GROUP);
            }
        }

        H5F.close(fileId);
        return result;
    }

    #endregion

    #region 检查工具

    public static bool DatasetExists(string h5Path, string datasetPath)
    {
        string fullPath = GetH5FullPath(h5Path);
        long fileId = H5F.open(fullPath, H5F.ACC_RDONLY);
        if (fileId < 0) return false;

        bool exists = H5L.exists(fileId, datasetPath) > 0;
        H5F.close(fileId);
        return exists;
    }

    #endregion
}
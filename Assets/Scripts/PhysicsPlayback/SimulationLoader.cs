using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public sealed class SimulationLoader
{
    public const string SupportedSchemaVersion = "1.0";

    public SimulationOutput Output { get; private set; }

    // 同步读取：Editor / Windows 打包可用。WebGL、Android 请走 PlaybackController.LoadAsync。
    public void Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Simulation file not found", path);
        LoadFromText(File.ReadAllText(path));
    }

    // 解析与校验只写一份，同步和异步两条路径共用同一套规则。
    public void LoadFromText(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Simulation JSON is empty");

        var output = JsonUtility.FromJson<SimulationOutput>(json);
        Validate(output);
        Output = output;
    }

    // 运行时重算（改参数后由 C# 求解器生成）走同一条校验，
    // 保证"内存里造出来的"和"从文件读进来的"一样可信，下游不用区分两种来源。
    public void SetOutput(SimulationOutput output)
    {
        Validate(output);
        Output = output;
    }

    public static void Validate(SimulationOutput output)
    {
        if (output == null || output.schema_version != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported simulation schema '{output?.schema_version}'");
        if (output.objects == null || output.objects.Length == 0)
            throw new InvalidDataException("Simulation declares no objects");
        if (output.frames == null || output.frames.Length == 0)
            throw new InvalidDataException("Simulation has no frames");

        // objects[] 是场景实体的唯一清单：ID 非空唯一，半径为正（否则 localScale 会被算成 0）。
        var ids = new HashSet<string>();
        foreach (var item in output.objects)
        {
            if (item == null || string.IsNullOrEmpty(item.id)) throw new InvalidDataException("Empty object ID");
            if (!ids.Add(item.id)) throw new InvalidDataException($"Duplicate object ID '{item.id}'");
            if (item.radius <= 0f) throw new InvalidDataException($"Object '{item.id}' has non-positive radius");
        }

        // 帧级校验：ID 必须已登记，时间必须单调递增（Seek 的线性扫描依赖这一前提）。
        float previous = float.NegativeInfinity;
        foreach (var frame in output.frames)
        {
            if (frame == null || frame.objects == null) throw new InvalidDataException("Frame missing objects[]");
            if (frame.time < previous) throw new InvalidDataException("Frame times are not monotonically increasing");
            previous = frame.time;
            foreach (var state in frame.objects)
            {
                if (state == null || !ids.Contains(state.id))
                    throw new InvalidDataException($"Frame references unknown object '{state?.id}'");
                if (state.position == null || state.position.Length < 2)
                    throw new InvalidDataException($"Object '{state.id}' has malformed position");
            }
        }
    }
}

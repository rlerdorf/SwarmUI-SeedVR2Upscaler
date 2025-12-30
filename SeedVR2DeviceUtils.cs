using System;
using System.Collections.Generic;
using System.Linq;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SeedVR2Upscaler;

/// <summary>Helper utilities for SeedVR2 device option discovery and selection.</summary>
public static class SeedVR2DeviceUtils
{
    /// <summary>Builds a list of possible offload devices.</summary>
    public static List<string> GetSeedVR2OffloadDeviceValues(Session session)
    {
        // Mirror SeedVR2 python behavior
        List<string> local = BuildLocalSeedVR2DeviceList().Select(v => $"{v}///{v}").ToList();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> merged = [];
        foreach (string v in local)
        {
            string raw = v.Before("///").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            if (seen.Add(raw))
            {
                merged.Add(v);
            }
        }
        return merged;
    }

    /// <summary>
    /// Local replica of SeedVR2's python get_device_list() behavior (memory_manager.get_device_list).
    /// </summary>
    public static List<string> BuildLocalSeedVR2DeviceList()
    {
        List<string> devs = [];
        bool hasCuda = false;
        bool hasMps = false;

        // CUDA: enumerate NVIDIA GPUs
        try
        {
            NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
            if (gpus is not null && gpus.Length > 0)
            {
                hasCuda = true;
                for (int i = 0; i < gpus.Length; i++)
                {
                    devs.Add($"cuda:{i}");
                }
            }
        }
        catch
        {
            // ignore
        }

        // MPS: best-effort detection (python checks torch.backends.mps.is_available()).
        // SwarmUI doesn't have torch, so we approximate: macOS => potentially mps.
        // This only affects the optional "cpu exclusion" behavior when MPS-only.
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                hasMps = true;
                devs.Add("mps");
            }
        }
        catch
        {
            // ignore
        }

        List<string> result = [];
        result.Add("none");
        // Mirror python logic: include cpu only if (has_cuda OR not has_mps)
        if (hasCuda || !hasMps)
        {
            result.Add("cpu");
        }
        result.AddRange(devs);
        return result.Count > 0 ? result : [];
    }

    /// <summary>
    /// Resolves the correct VAE offload device string to send to ComfyUI nodes.
    /// The param is toggleable: if the user did not enable it, a reasonable device is selected automatically.
    /// </summary>
    public static string ResolveSeedVR2VAEOffloadDevice(WorkflowGenerator g, T2IRegisteredParam<string> offloadParam, bool tiledVAE, bool cacheModel)
    {
        // Local device options are derived from SwarmUI-side detection and should match SeedVR2 python's get_device_list()
        // as closely as possible (without importing torch)
        List<string> localList = BuildLocalSeedVR2DeviceList();
        HashSet<string> localAllowed = new(localList, StringComparer.OrdinalIgnoreCase);

        string resolved;
        if (g.UserInput.TryGet(offloadParam, out string chosen))
        {
            resolved = chosen.Before("///").Trim();
        }
        else
        {
            // Param not enabled (or not present): choose a sane device automatically
            HashSet<string> allowed = localAllowed;
            if (cacheModel)
            {
                if (allowed.Contains("cpu"))
                {
                    resolved = "cpu";
                }
                else
                {
                    resolved = localList.FirstOrDefault(v => !v.Equals("none", StringComparison.OrdinalIgnoreCase)) ?? "none";
                }
            }
            else if (tiledVAE)
            {
                resolved = allowed.Contains("cpu")
                    ? "cpu"
                    : (localList.FirstOrDefault(v => !v.Equals("none", StringComparison.OrdinalIgnoreCase)) ?? "none");
            }
            else
            {
                resolved = "none";
            }
        }

        if (localAllowed.Count > 0 && !localAllowed.Contains(resolved))
        {
            string valid = localAllowed.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).JoinString(", ");
            throw new SwarmUserErrorException($"SeedVR2: Invalid VAE offload device '{resolved}'. Valid values (locally detected) are: {valid}.");
        }

        if (cacheModel && resolved.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new SwarmUserErrorException("SeedVR2: 'SeedVR2 Cache Model' requires 'SeedVR2 VAE Offload Device' to be set (for example 'cpu' or 'cuda:0').");
        }

        return resolved;
    }
}



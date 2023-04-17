using System;

using ProcessArchitecture = System.Runtime.InteropServices.Architecture;

namespace Microsoft.Diagnostics.Runtime;

/// <summary>
/// The architecture of a process.
/// </summary>
public enum Architecture
{
    /// <summary>
    /// Unknown.  Should never be exposed except in case of error.
    /// </summary>
    Unknown,

    /// <summary>
    /// x86.
    /// </summary>
    X86,

    /// <summary>
    /// x64
    /// </summary>
    Amd64,

    /// <summary>
    /// ARM
    /// </summary>
    Arm,

    /// <summary>
    /// ARM64
    /// </summary>
    Arm64
}

public static class ArchitectureExtension
{
    public static Architecture ToArchitecture(this ProcessArchitecture architecture)
    {
        return architecture switch
        {
            ProcessArchitecture.X86 => Architecture.X86,
            ProcessArchitecture.X64 => Architecture.Amd64,
            ProcessArchitecture.Arm => Architecture.Arm,
            ProcessArchitecture.Arm64 => Architecture.Arm64,
            _ => Architecture.Unknown
        };
    }
}
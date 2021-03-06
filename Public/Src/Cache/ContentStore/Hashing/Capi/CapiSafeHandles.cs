// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace BuildXL.Cache.ContentStore.Hashing
{
    /// <summary>
    ///     SafeHandle base class for CAPI handles (such as HCRYPTKEY and HCRYPTHASH) which must keep their
    ///     CSP alive as long as they stay alive as well. CAPI requires that all child handles belonging to a
    ///     HCRYPTPROV must be destroyed up before the reference count to the HCRYPTPROV drops to zero.
    ///     Since we cannot control the order of finalization between the two safe handles, SafeCapiHandleBase
    ///     maintains a native refcount on its parent HCRYPTPROV to ensure that if the corresponding
    ///     SafeCspKeyHandle is finalized first CAPI still keeps the provider alive.
    /// </summary>
#if FEATURE_CORESYSTEM
    [System.Security.SecurityCritical]
#else
#pragma warning disable 618    // Have not migrated to v4 transparency yet
    [SecurityCritical(SecurityCriticalScope.Everything)]
#pragma warning restore 618
#endif
    internal abstract class SafeCapiHandleBase : SafeHandleZeroOrMinusOneIsInvalid
    {
        private IntPtr m_csp;

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        internal SafeCapiHandleBase() : base(true)
        {
        }

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        [DllImport("advapi32", SetLastError = true)]
#if !FEATURE_CORESYSTEM
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
#endif
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptContextAddRef(IntPtr hProv,
                                                      IntPtr pdwReserved,
                                                      int dwFlags);

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        [DllImport("advapi32")]
#if !FEATURE_CORESYSTEM
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
#endif
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptReleaseContext(IntPtr hProv, int dwFlags);


        protected IntPtr ParentCsp
        {

#if !FEATURE_CORESYSTEM
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
#endif
            set
            {
                // We should not be resetting the parent CSP if it's already been set once - that will
                // lead to leaking the original handle.
                Debug.Assert(m_csp == IntPtr.Zero);

                int error = (int)CapiNative.ErrorCode.Success;

                // A successful call to CryptContextAddRef and an assignment of the handle value to our field
                // SafeHandle need to happen atomically, so we contain them within a CER. 
                RuntimeHelpers.PrepareConstrainedRegions();
                try { }
                finally
                {
                    if (CryptContextAddRef(value, IntPtr.Zero, 0))
                    {
                        m_csp = value;
                    }
                    else
                    {
                        error = Marshal.GetLastWin32Error();
                    }
                }

                if (error != (int)CapiNative.ErrorCode.Success)
                {
                    throw new CryptographicException(error);
                }
            }
        }

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
#if !FEATURE_CORESYSTEM
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
#endif
        internal void SetParentCsp(SafeCspHandle parentCsp)
        {
            bool addedRef = false;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                parentCsp.DangerousAddRef(ref addedRef);
                IntPtr rawParentHandle = parentCsp.DangerousGetHandle();
                ParentCsp = rawParentHandle;
            }
            finally
            {
                if (addedRef)
                {
                    parentCsp.DangerousRelease();
                }
            }
        }

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        protected abstract bool ReleaseCapiChildHandle();

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        protected sealed override bool ReleaseHandle()
        {
            // Order is important here - we must destroy the child handle before the parent CSP
            bool destroyedChild = ReleaseCapiChildHandle();
            bool releasedCsp = true;

            if (m_csp != IntPtr.Zero)
            {
                releasedCsp = CryptReleaseContext(m_csp, 0);
            }

            return destroyedChild && releasedCsp;
        }
    }

    /// <summary>
    ///     SafeHandle for CAPI hash algorithms (HCRYPTHASH)
    /// </summary>
#if FEATURE_CORESYSTEM
    [System.Security.SecurityCritical]
#else
#pragma warning disable 618    // Have not migrated to v4 transparency yet
    [System.Security.SecurityCritical(System.Security.SecurityCriticalScope.Everything)]
#pragma warning restore 618
#endif
    internal sealed class SafeCapiHashHandle : SafeCapiHandleBase
    {

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        private SafeCapiHashHandle()
        {
        }

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        [DllImport("advapi32")]
#if !FEATURE_CORESYSTEM
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
#endif
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptDestroyHash(IntPtr hHash);

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        protected override bool ReleaseCapiChildHandle()
        {
            return CryptDestroyHash(handle);
        }
    }

    /// <summary>
    ///     SafeHandle for CAPI keys (HCRYPTKEY)
    /// </summary>
#if FEATURE_CORESYSTEM
    [System.Security.SecurityCritical]
#else
#pragma warning disable 618    // Have not migrated to v4 transparency yet
    [System.Security.SecurityCritical(System.Security.SecurityCriticalScope.Everything)]
#pragma warning restore 618
#endif
    internal sealed class SafeCapiKeyHandle : SafeCapiHandleBase
    {
        private static volatile SafeCapiKeyHandle? s_invalidHandle;

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        private SafeCapiKeyHandle()
        {
        }

        /// <summary>
        ///     NULL key handle
        /// </summary>
        internal static SafeCapiKeyHandle InvalidHandle
        {
            get
            {
                if (s_invalidHandle == null)
                {
                    // More than one of these might get created in parallel, but that's okay.
                    // Saving one to the field saves on GC tracking, but by SuppressingFinalize on
                    // any instance returned there's already less finalization pressure.
                    SafeCapiKeyHandle handle = new SafeCapiKeyHandle();
                    handle.SetHandle(IntPtr.Zero);
                    GC.SuppressFinalize(handle);
                    s_invalidHandle = handle;
                }

                return s_invalidHandle;
            }
        }

        [DllImport("advapi32")]
#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#else
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
#endif
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptDestroyKey(IntPtr hKey);

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        protected override bool ReleaseCapiChildHandle()
        {
            return CryptDestroyKey(handle);
        }
    }

    /// <summary>
    ///     SafeHandle for crypto service providers (HCRYPTPROV)
    /// </summary>
#if FEATURE_CORESYSTEM
    [System.Security.SecurityCritical]
#else
#pragma warning disable 618    // Have not migrated to v4 transparency yet
    [System.Security.SecurityCritical(System.Security.SecurityCriticalScope.Everything)]
#pragma warning restore 618
#endif
    internal sealed class SafeCspHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        private SafeCspHandle() : base(true)
        {
            return;
        }

        [DllImport("advapi32")]
#if !FEATURE_CORESYSTEM
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
#endif
        [SuppressUnmanagedCodeSecurity]
#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptReleaseContext(IntPtr hProv, int dwFlags);

#if FEATURE_CORESYSTEM
        [System.Security.SecurityCritical]
#endif
        protected override bool ReleaseHandle()
        {
            return CryptReleaseContext(handle, 0);
        }
    }
}

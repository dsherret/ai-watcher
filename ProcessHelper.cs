#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;

namespace AIWatcher;

/// <summary>
/// Reads the current working directory of another process via its PEB.
/// </summary>
public static class ProcessHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        uint processInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, nint nSize, out nint lpNumberOfBytesRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    public static string? GetCurrentDirectory(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            // get PEB address
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref pbi,
                (uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;

            var ptrBuf = new byte[8];

            // read ProcessParameters pointer from PEB + 0x20
            if (!ReadProcessMemory(handle, pbi.PebBaseAddress + 0x20, ptrBuf, 8, out _))
                return null;
            var processParams = new IntPtr(BitConverter.ToInt64(ptrBuf, 0));

            // read CurrentDirectory.DosPath.Length (USHORT) at ProcessParameters + 0x38
            var lenBuf = new byte[2];
            if (!ReadProcessMemory(handle, processParams + 0x38, lenBuf, 2, out _))
                return null;
            var length = BitConverter.ToUInt16(lenBuf, 0);
            if (length == 0) return null;

            // read CurrentDirectory.DosPath.Buffer pointer at ProcessParameters + 0x40
            if (!ReadProcessMemory(handle, processParams + 0x40, ptrBuf, 8, out _))
                return null;
            var bufferPtr = new IntPtr(BitConverter.ToInt64(ptrBuf, 0));

            // read the actual directory string
            var strBuf = new byte[length];
            if (!ReadProcessMemory(handle, bufferPtr, strBuf, length, out _))
                return null;

            return Encoding.Unicode.GetString(strBuf).TrimEnd('\\');
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
#endif

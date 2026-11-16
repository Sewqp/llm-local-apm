import ctypes, socket
from ctypes import wintypes
kernel=ctypes.windll.kernel32
kernel.OpenProcess.restype=wintypes.HANDLE
kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
kernel.GetProcessTimes.argtypes=[wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
kernel.CloseHandle.argtypes=[wintypes.HANDLE]
class MemoryStatus(ctypes.Structure):
    _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [(n, ctypes.c_ulonglong) for n in ('total', 'available', 'pageTotal', 'pageAvailable', 'virtualTotal', 'virtualAvailable', 'extended')]

def memory():
    s = MemoryStatus()
    s.length = ctypes.sizeof(s)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(s))
    return {'availableMb': round(s.available / 1048576, 2), 'loadPercent': s.load}

class TcpRow(ctypes.Structure):
    _fields_ = [(n, ctypes.c_ulong) for n in ('state', 'localAddress', 'localPort', 'remoteAddress', 'remotePort', 'pid')]

class ProcessMemory(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [(n, ctypes.c_size_t) for n in ('PeakWorkingSetSize', 'WorkingSetSize', 'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage', 'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]

def process(pid):
    h = kernel.OpenProcess(4096 | 16, False, pid)
    if not h:
        return {'error': ctypes.GetLastError()}
    try:
        m = ProcessMemory()
        m.cb = ctypes.sizeof(m)
        ctypes.windll.psapi.GetProcessMemoryInfo(h, ctypes.byref(m), m.cb)
        ts = [wintypes.FILETIME() for _ in range(4)]
        kernel.GetProcessTimes(h, *[ctypes.byref(x) for x in ts])
        cpu = sum(((t.dwHighDateTime << 32) + t.dwLowDateTime for t in ts[2:])) / 10000000
        return {'workingSetMb': round(m.WorkingSetSize / 1048576, 2), 'privateMb': round(m.PrivateUsage / 1048576, 2), 'cpuSeconds': cpu}
    finally:
        kernel.CloseHandle(h)

def tcp(pid, port):
    length = ctypes.c_ulong(0)
    api = ctypes.windll.iphlpapi.GetExtendedTcpTable
    api(None, ctypes.byref(length), False, 2, 5, 0)
    buffer = ctypes.create_string_buffer(length.value)
    error = api(buffer, ctypes.byref(length), False, 2, 5, 0)
    if error:
        return {'error': error}
    rows = (TcpRow * ctypes.c_ulong.from_buffer(buffer).value).from_buffer(buffer, ctypes.sizeof(ctypes.c_ulong))
    return {'serverEstablished': sum((r.state == 5 and r.pid == pid and (socket.ntohs(r.localPort & 65535) == port) for r in rows)), 'systemTimeWait': sum((r.state == 11 for r in rows))}

def system_times():
    ts = [wintypes.FILETIME() for _ in range(3)]
    if not kernel.GetSystemTimes(*[ctypes.byref(t) for t in ts]):
        return None
    return [((t.dwHighDateTime << 32) + t.dwLowDateTime) / 10000000.0 for t in ts]
ctypes.windll.psapi.GetProcessMemoryInfo.argtypes=[wintypes.HANDLE,ctypes.POINTER(ProcessMemory),wintypes.DWORD]

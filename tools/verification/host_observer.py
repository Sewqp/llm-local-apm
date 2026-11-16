"""Read-only Windows performance counters and bounded process CPU attribution."""
import ctypes,time
from ctypes import wintypes as w
class ValueUnion(ctypes.Union):
    _fields_=[('longValue',w.LONG),('doubleValue',ctypes.c_double),('largeValue',ctypes.c_longlong)]
class CounterValue(ctypes.Structure):
    _fields_=[('status',w.DWORD),('value',ValueUnion)]
class ProcessEntry(ctypes.Structure):
    _fields_=[('size',w.DWORD),('usage',w.DWORD),('pid',w.DWORD),('heap',ctypes.c_size_t),('module',w.DWORD),
              ('threads',w.DWORD),('parent',w.DWORD),('priority',w.LONG),('flags',w.DWORD),('name',w.WCHAR*260)]
class HostObserver:
    def __init__(self):
        self.pdh=ctypes.WinDLL('pdh');self.query=w.HANDLE();self.counters={};self.previous={};self.tick=time.perf_counter()
        self.pdh.PdhOpenQueryW.argtypes=[w.LPCWSTR,ctypes.c_size_t,ctypes.POINTER(w.HANDLE)]
        self.pdh.PdhAddEnglishCounterW.argtypes=[w.HANDLE,w.LPCWSTR,ctypes.c_size_t,ctypes.POINTER(w.HANDLE)]
        self.pdh.PdhCollectQueryData.argtypes=[w.HANDLE]
        self.pdh.PdhGetFormattedCounterValue.argtypes=[w.HANDLE,w.DWORD,ctypes.POINTER(w.DWORD),ctypes.POINTER(CounterValue)]
        self.pdh.PdhCloseQuery.argtypes=[w.HANDLE]
        status=self.pdh.PdhOpenQueryW(None,0,ctypes.byref(self.query))
        if status:raise OSError('PdhOpenQuery '+str(status))
        names={'cpu':r'\Processor(_Total)\% Processor Time','privileged':r'\Processor(_Total)\% Privileged Time',
               'dpc':r'\Processor(_Total)\% DPC Time','interrupt':r'\Processor(_Total)\% Interrupt Time',
               'runQueue':r'\System\Processor Queue Length','pageReads':r'\Memory\Page Reads/sec',
               'pagesInput':r'\Memory\Pages Input/sec','availableMb':r'\Memory\Available MBytes'}
        for name,path in names.items():
            counter=w.HANDLE()
            if not self.pdh.PdhAddEnglishCounterW(self.query,path,0,ctypes.byref(counter)):self.counters[name]=counter
        self.pdh.PdhCollectQueryData(self.query)
        self.k=ctypes.windll.kernel32
        self.k.CreateToolhelp32Snapshot.argtypes=[w.DWORD,w.DWORD];self.k.CreateToolhelp32Snapshot.restype=w.HANDLE
        self.k.Process32FirstW.argtypes=[w.HANDLE,ctypes.POINTER(ProcessEntry)]
        self.k.Process32NextW.argtypes=[w.HANDLE,ctypes.POINTER(ProcessEntry)]
        self.k.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];self.k.OpenProcess.restype=w.HANDLE
        self.k.GetProcessTimes.argtypes=[w.HANDLE]+[ctypes.POINTER(w.FILETIME)]*4
        self.k.CloseHandle.argtypes=[w.HANDLE]
    def sample(self):
        self.pdh.PdhCollectQueryData(self.query);result={}
        for name,counter in self.counters.items():
            value=CounterValue()
            status=self.pdh.PdhGetFormattedCounterValue(counter,0x200,None,ctypes.byref(value))
            if status==0 and value.status in (0,1):result[name]=value.value.doubleValue
        now=time.perf_counter();elapsed=now-self.tick;self.tick=now
        current={};top=[];snap=self.k.CreateToolhelp32Snapshot(2,0);entry=ProcessEntry();entry.size=ctypes.sizeof(entry)
        try:
            valid=self.k.Process32FirstW(snap,ctypes.byref(entry))
            while valid:
                h=self.k.OpenProcess(0x1000,False,entry.pid)
                if h:
                    try:
                        values=[w.FILETIME() for _ in range(4)]
                        if self.k.GetProcessTimes(h,*[ctypes.byref(v) for v in values]):
                            ticks=sum((v.dwHighDateTime<<32)+v.dwLowDateTime for v in values[2:])/1e7
                            created=(values[0].dwHighDateTime<<32)+values[0].dwLowDateTime
                            key=(entry.pid,created);current[key]=ticks
                            if key in self.previous:top.append({'pid':entry.pid,'name':entry.name,'cpuSeconds':ticks-self.previous[key]})
                    finally:self.k.CloseHandle(h)
                valid=self.k.Process32NextW(snap,ctypes.byref(entry))
        finally:self.k.CloseHandle(snap)
        self.previous=current
        result.update(intervalSeconds=elapsed,topProcesses=sorted(top,key=lambda p:p['cpuSeconds'],reverse=True)[:6])
        return result
    def close(self):self.pdh.PdhCloseQuery(self.query)

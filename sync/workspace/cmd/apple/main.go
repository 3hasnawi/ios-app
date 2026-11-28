package main

// #include <stdlib.h>
// typedef void (*transfer_callback)(int, long long, long long);
// static inline void notify_transfer(transfer_callback callback, int stage, long long done, long long total) { callback(stage, done, total); }
import "C"
import (
	"payroll/workspace"
	"unsafe"
)

//export payroll_sync
func payroll_sync(input *C.char, callback C.transfer_callback) *C.char {
	notify := func(active bool) {
		var value C.int
		if active {
			value = 1
		}
		C.notify_transfer(callback, value, 0, 0)
	}
	progress := func(phase int, done, total int64) {
		C.notify_transfer(callback, C.int(phase+1), C.longlong(done), C.longlong(total))
	}
	return C.CString(string(workspace.CallWithProgress([]byte(C.GoString(input)), notify, progress)))
}

//export payroll_sync_free
func payroll_sync_free(p *C.char) { C.free(unsafe.Pointer(p)) }
func main()                       {}

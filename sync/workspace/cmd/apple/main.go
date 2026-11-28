package main

// #include <stdlib.h>
// typedef void (*transfer_callback)(int);
// static inline void notify_transfer(transfer_callback callback, int active) { callback(active); }
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
		C.notify_transfer(callback, value)
	}
	return C.CString(string(workspace.CallWithTransfer([]byte(C.GoString(input)), notify)))
}

//export payroll_sync_free
func payroll_sync_free(p *C.char) { C.free(unsafe.Pointer(p)) }
func main()                       {}

package main

import (
	"io"
	"os"
	"payroll/workspace"
)

func main() {
	data, e := io.ReadAll(io.LimitReader(os.Stdin, 32<<20))
	if e != nil {
		os.Exit(1)
	}
	os.Stdout.Write(workspace.Call(data))
}

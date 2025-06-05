//#r "paket:
//      nuget Yzl >= 2.0.0"
#r "paket: github queil/yzl src/Yzl/Yzl.fs"
#load @"queil/yzl/src/Yzl/Yzl.fs"

#r "../bin/Debug/net9.0/Paket.Core.dll"


open Paket.Core

open Yzl



let args = System.Environment.CommandLine

printfn $"%s{args}"
printfn $"%s{System.IO.Directory.GetCurrentDirectory()}"


let trees = Yzl.seq

trees [ "oak"; "pine"; "spruce"; "john"; "michigan" ]
|> Yzl.render
|> printf "%s"

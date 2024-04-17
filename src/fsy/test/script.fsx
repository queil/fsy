#r "paket: 
      nuget Yzl >= 2.0.0"
//#r "paket: github queil/yzl:main src/Yzl/Yzl.fs"
//#load @"queil/yzl/src/Yzl/Yzl.fs"

open Yzl

let trees = Yzl.seq

trees [ "oak"; "pine"; "spruce"; "john"; "michigan" ]
|> Yzl.render
|> printf "%s"

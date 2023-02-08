open Queil.FSharp.FscHost
open System.Text.Json
open System.IO
open Fsy.Cli
open System.Diagnostics
open System.Reflection
open System.Runtime.Versioning

let sw = Stopwatch.StartNew()

let cmd = Args.FromCmdLine()

let compilerOptions =
  { CompilerOptions.Default with
      IncludeHostEntryAssembly = false
      Target = "exe"
      Standalone = true
      Args =
        fun scriptPath refs opts ->
          [ "--noframework"
            "--nowin32manifest"
            yield! CompilerOptions.Default.Args scriptPath refs opts ] }

let outDir = cmd.TryGetResult Output_Dir |> Option.defaultValue "./out"

if cmd.Contains Clean && Directory.Exists outDir then
  Directory.Delete(outDir, true)

let options =
  { Options.Default with
      Compiler = compilerOptions
      Logger =
        if cmd.Contains Verbose then
          fun msg -> printfn $"{sw.Elapsed}: {msg}"
        else
          ignore
      AutoLoadNugetReferences = false
      UseCache = true
      CacheDir = outDir }

let scriptFilePath = cmd.GetResult ScriptFilePath

let beforeCompile = sw.ElapsedMilliseconds

let output =
  CompilerHost.getAssembly options (scriptFilePath |> Queil.FSharp.FscHost.File)
  |> Async.RunSynchronously

printfn $"fsc-host: {sw.ElapsedMilliseconds - beforeCompile} ms"

let dotnetVersion =
  Assembly
    .GetEntryAssembly()
    .GetCustomAttribute<TargetFrameworkAttribute>()
    .FrameworkName
  |> fun s -> s.Split("=v")[1]

let runtimeconfig =
  JsonSerializer.Serialize
    {| runtimeOptions =
        {| tfm = $"net{dotnetVersion}"
           framework =
            {| name = "Microsoft.NETCore.App"
               version = $"{dotnetVersion}.0" |} |} |}

System.IO.File.WriteAllText(
  $"""{Path.ChangeExtension(output.AssemblyFilePath, ".runtimeconfig.json")}""",
  runtimeconfig
)

if cmd.Contains Run then
  output.Assembly.Value.EntryPoint.Invoke(null, Array.empty) |> ignore

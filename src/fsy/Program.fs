open Argu
open Queil.FSharp.FscHost
open System.Text.Json
open System.IO
open Fsy.Cli
open System
open System.Diagnostics
open System.Reflection
open System.Runtime.Versioning

Environment.ExitCode <- 1

let sw = Stopwatch.StartNew()

try
  let cmd = Args.FromCmdLine()
  let verbose = cmd.Contains Verbose

  let installFsxExtensions () =
    let targetDir =
      Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".fsharp",
        "fsx-extensions",
        ".fsch"
      )

    Directory.CreateDirectory(targetDir) |> ignore
    let sourceDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)

    for (sourcePath, targetPath) in
      Directory.EnumerateFiles(sourceDir, "*.dll")
      |> Seq.map FileInfo
      |> Seq.map (fun f -> Path.Combine(sourceDir, f.Name), Path.Combine(targetDir, f.Name)) do
      File.Copy(sourcePath, targetPath, true)

  let compileScript (args: ParseResults<ScriptArgs>) (script: Queil.FSharp.FscHost.Script) =
    let compilerOptions =
      { CompilerOptions.Default with
          IncludeHostEntryAssembly = false
          Target = "exe"
          Standalone = false
          LangVersion = Some "preview"
          Args =
            fun scriptPath refs opts ->
              [ "--noframework"
                "--nowin32manifest"
                yield! CompilerOptions.Default.Args scriptPath refs opts ] }

    let cacheDir =
      Path.GetFullPath(args.TryGetResult Cache_Dir |> Option.defaultValue "./.fsy")

    if args.Contains Force then

      if Directory.Exists cacheDir then
        if verbose then
          printfn $"Deleting directory %s{cacheDir} recursively..."

        Directory.Delete(cacheDir, true)

      let fschDir =
        Path.Combine(
          Path.GetDirectoryName(
            match script with
            | File path -> path
            | Inline _ -> "inline"
          ),
          ".fsch"
        )

      if Directory.Exists fschDir then
        if verbose then
          printfn $"Deleting directory %s{fschDir} recursively..."

        Directory.Delete(fschDir, true)

    let options =
      { Options.Default with
          Compiler = compilerOptions
          Logger =
            if verbose then
              fun msg -> printfn $"{sw.Elapsed}: {msg}"
            else
              ignore
          AutoLoadNugetReferences = cmd.Contains Run
          UseCache = true
          CacheDir = cacheDir }

    let beforeCompile = sw.ElapsedMilliseconds
    let output = CompilerHost.getAssembly options script |> Async.RunSynchronously

    if verbose then
      printfn $"fsc-host: {sw.ElapsedMilliseconds - beforeCompile} ms"

    output

  let getScript (args: ParseResults<ScriptArgs>) =
    let scriptFullPath = Path.GetFullPath(args.GetResult Script)
    Queil.FSharp.FscHost.File(scriptFullPath)

  match cmd.GetSubCommand() with
  | Run args ->
    let script = args |> getScript
    let output = compileScript args script
    output.Assembly.Value.EntryPoint.Invoke(null, Array.empty) |> ignore
    ()
  | Compile args ->
    let script = args |> getScript
    let output = compileScript args script

    let defaultOutDir =
      match script with
      | File f -> Path.GetFileNameWithoutExtension(f)
      | Inline _ -> "inline"

    let outDir = args.GetResult(Output_Dir, $"./{defaultOutDir}")
    Directory.CreateDirectory(outDir) |> ignore
    let outName = DirectoryInfo(outDir).Name

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

    let rtConfigPath = $"{Path.Combine(outDir, outName)}.runtimeconfig.json"
    File.WriteAllText(rtConfigPath, runtimeconfig)
    File.Copy(output.AssemblyFilePath, $"{Path.Combine(outDir, outName)}.dll", true)
  | InstallFsxExtensions -> installFsxExtensions ()
  | _ -> ()

with
| :? Argu.ArguParseException as exn -> printfn "%s" exn.Message
| :? ScriptCompileError as exn ->
  use _ =
    { new IDisposable with
        member _.Dispose() = Console.ResetColor() }

  Console.ForegroundColor <- ConsoleColor.Red
  exn.Diagnostics |> Seq.iter (System.Console.Error.WriteLine)

open Argu
open Queil.FSharp.FscHost
open Queil.FSharp.Hashing
open System.Text.Json
open System.IO
open Fsy.Cli
open System
open System.Reflection
open System.Runtime.Versioning
open System.Diagnostics

Environment.ExitCode <- 1

let rawCmd = Environment.GetCommandLineArgs() |> Seq.toList |> (fun l -> l[1..])
let indexOfDoubleDash = rawCmd |> List.tryFindIndex (fun f -> f = "--")

let fsyArgs, passThruArgs =
  match indexOfDoubleDash with
  | Some idx ->
    let fsyArgs, scriptArgs = rawCmd |> List.splitAt idx
    fsyArgs, scriptArgs[1..]
  | None -> rawCmd |> Seq.toList, []

let parser = ArgumentParser.Create<Args>(errorHandler = ProcessExiter())

let cmd = parser.Parse(fsyArgs |> Seq.toArray)
let verbose = cmd.Contains Verbose

let useConsoleColor color =
  Console.ForegroundColor <- color

  { new IDisposable with
      member _.Dispose() = Console.ResetColor() }

try

  let installFsxExtensions () =
    let targetDir =
      Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
        ".fsharp",
        "fsx-extensions",
        ".fsch"
      )

    Directory.CreateDirectory targetDir |> ignore
    let sourceDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)

    for sourcePath, targetPath in
      Directory.EnumerateFiles(sourceDir, "*.dll")
      |> Seq.map FileInfo
      |> Seq.filter (fun f -> f.Name.StartsWith "FSharp." |> not)
      |> Seq.map (fun f -> Path.Combine(sourceDir, f.Name), Path.Combine(targetDir, f.Name)) do
      File.Copy(sourcePath, targetPath, true)

  let compileScript (args: ParseResults<ScriptArgs>) (originalFilePath: string) =
    let compilerOptions =
      { CompilerOptions.Default with
          IncludeHostEntryAssembly = false
          Target = "exe"
          Standalone = false
          LangVersion = Some "preview"
          Symbols = args.GetResults Symbol
          Args =
            fun scriptPath refs opts ->
              [ "--noframework"
                "--nowin32manifest"
                yield! CompilerOptions.Default.Args scriptPath refs opts ] }

    let cacheDirOverride = args.TryGetResult Cache_Dir |> Option.map Path.GetFullPath

    if args.Contains Force then

      let hashes = Hash.fileHash originalFilePath None

      let rootDir =
        match cacheDirOverride with
        | Some cacheRootDir ->
          cacheRootDir
        | None -> Path.Combine(Path.GetTempPath(), ".fsch")
      
      let cacheDir =
        if args.Contains ContentAddressableCache then
          hashes.ContentHashedScriptDir rootDir
        else
          hashes.HashedScriptDir rootDir

      printfn $"Attempting to delete cache: %s{cacheDir}"

      if Directory.Exists cacheDir then
        if verbose then
          printfn $"Deleting directory %s{cacheDir} recursively..."

        Directory.Delete(cacheDir, true)
      else
        printfn $"Directory not found (skipping): %s{cacheDir}"

    let options =
      { Options.Default with
          Compiler = compilerOptions
          Verbose = verbose
          Logger = if verbose then Some(fun msg -> printfn $"{msg}") else None
          AutoLoadNugetReferences = cmd.Contains Run
          UseCache = true
          CacheIsolation =
            if args.Contains ContentAddressableCache then
              No
            else
              PerRootScript }
      |> fun opts ->
        match cacheDirOverride with
        | Some cacheDir -> { opts with OutputDir = cacheDir }
        | None -> opts

    let newFilePath, movedWithExtension =
      match originalFilePath with
      | path when path |> Path.HasExtension -> path, false
      | path ->
        let newPath =
          Path.Combine(Path.GetDirectoryName path, $"""{path |> File.ReadAllText |> Hash.sha256 |> Hash.short}.fsx""")

        printfn $"Shadowing file %s{originalFilePath} to %s{newPath}"
        File.Copy(path, newPath)
        newPath, true

    try
      let sw = Stopwatch.StartNew()

      let output =
        CompilerHost.getAssembly options (Queil.FSharp.FscHost.File newFilePath)
        |> Async.RunSynchronously

      if verbose then
        printfn $"fsch: {sw.ElapsedMilliseconds} ms"

      output
    finally
      if movedWithExtension then
        if verbose then
          printfn $"Deleting shadowed file: {newFilePath}"

        File.Delete newFilePath

  let getScript (args: ParseResults<ScriptArgs>) = Path.GetFullPath(args.GetResult Script)

  let compile args =
    let script = args |> getScript
    let output = compileScript args script
    let defaultOutDir = Path.GetFileNameWithoutExtension script
    let outDir = args.GetResult(Output_Dir, $"./{defaultOutDir}")
    Directory.CreateDirectory outDir |> ignore
    let outName = DirectoryInfo(outDir).Name

    let dotnetVersion =
      Assembly.GetEntryAssembly().GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName
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
    let outputFile = $"{Path.Combine(outDir, outName)}.dll"
    File.Copy(output.AssemblyFilePath, outputFile, true)
    outputFile

  match cmd.GetSubCommand() with
  | Run args ->
    let script = args |> getScript
    let output = compileScript args script
    output.Assembly.Value.EntryPoint.Invoke(null, Array.empty) |> ignore
    ()
  | Compile args ->
    compile args |> ignore
    ()
  | Install_Fsx_Extensions -> installFsxExtensions ()
  | _ -> ()

  Environment.ExitCode <- 0

with
| :? DirectoryNotFoundException as exn ->
  use _ = useConsoleColor ConsoleColor.Red
  let msg = if verbose then exn.ToString() else $"ERROR: {exn.Message}"
  msg |> Console.Error.WriteLine
| :? FileNotFoundException as exn ->
  use _ = useConsoleColor ConsoleColor.Red
  let msg = if verbose then exn.ToString() else $"ERROR: {exn.Message}"
  msg |> Console.Error.WriteLine
| :? ScriptCompileError as exn ->
  use _ = useConsoleColor ConsoleColor.Red
  exn.Diagnostics |> Seq.iter Console.Error.WriteLine
| :? ScriptParseError as exn ->
  use _ = useConsoleColor ConsoleColor.Red
  exn.Diagnostics |> Seq.iter Console.Error.WriteLine
| :? TargetInvocationException as exn ->
  use _ = useConsoleColor ConsoleColor.Red

  let msg =
    if verbose then
      exn.ToString()
    else
      $"ERROR: {exn.InnerException.Message}"

  msg |> Console.Error.WriteLine

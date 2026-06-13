open Argu
open Queil.FSharp.FscHost
open Queil.FSharp.Hashing
open System.IO
open Fsy.Cli
open System
open System.Reflection
open System.Diagnostics

Environment.ExitCode <- 1

let version, sha =
  let chunks =
    Assembly
      .GetExecutingAssembly()
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
      .InformationalVersion.Split("+")

  chunks[0], chunks[1]

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

let verbose =
  cmd.Contains Verbose
  || Environment.GetEnvironmentVariable "FSY_VERBOSE"
     |> Option.ofObj
     |> Option.contains "1"

let useConsoleColor color =
  Console.ForegroundColor <- color

  { new IDisposable with
      member _.Dispose() = Console.ResetColor() }

try

  let installFsxExtensions (path: string option) (framework: string option) =
    let targetDir =
      path
      |> Option.defaultValue (
        Path.Combine(
          Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
          ".fsharp",
          "fsx-extensions",
          ".fsch"
        )
      )

    Directory.CreateDirectory targetDir |> ignore

    let entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)

    let rec findToolsDir (dir: string) =
      let parent = Directory.GetParent(dir)

      if isNull parent then None
      elif parent.Name = "tools" then Some parent.FullName
      else findToolsDir parent.FullName

    let locateTfm (requested: string) =
      match findToolsDir entryDir with
      | None ->
        eprintfn $"WARN: Could not locate tool tfm: %s{requested}. Falling back to: %s{entryDir}"
        entryDir
      | Some toolsDir ->
        let dir = Path.Combine(toolsDir, requested)

        if Directory.Exists dir then
          dir
        else
          failwithf
            "tfm %s not packaged; have: %s"
            requested
            (Directory.GetDirectories toolsDir
             |> Array.map Path.GetFileName
             |> String.concat ", ")

    let sourceDir =
      match framework with
      | Some f -> locateTfm f
      | _ -> entryDir

    printfn $"Copying from: %s{sourceDir}"
    
    for sourcePath, targetPath in
      Directory.EnumerateFiles(sourceDir, "*.dll")
      |> Seq.map FileInfo
      |> Seq.filter (fun f -> f.Name.StartsWith "FSharp." |> not)
      |> Seq.map (fun f -> Path.Combine(sourceDir, f.Name), Path.Combine(targetDir, f.Name)) do
      eprintfn $"Copy: %s{sourcePath} -> %s{targetPath}"
      File.Copy(sourcePath, targetPath, true)

    printfn "Done"

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

    let cacheDirRoot =
      args.TryGetResult Cache_Dir
      |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "FSY_CACHE_DIR" |> Option.ofObj)
      |> Option.defaultValue (Path.GetTempPath())
      |> fun p -> Path.Combine(p, ".fsy", $"%s{version}+%s{sha[..7]}")
      |> Path.GetFullPath

    let options =
      { Options.Default with
          Compiler = compilerOptions
          Verbose = verbose
          Logger = if verbose then Some(fun msg -> printfn $"{msg}") else None
          AutoLoadNugetReferences = cmd.Contains Run
          UseCache = true }
      |> fun opts -> { opts with OutputDir = cacheDirRoot }

    let newFilePath, movedWithExtension =
      match originalFilePath with
      | path when path |> Path.HasExtension -> path, false
      | path ->
        let newPath =
          let scriptDir =
            args.TryGetResult Shadow_Dir
            |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "FSY_SHADOW_DIR" |> Option.ofObj)
            |> Option.map (Directory.CreateDirectory >> _.FullName)
            |> Option.defaultValue (Path.GetDirectoryName path)

          Path.Combine(scriptDir, $"""{path |> File.ReadAllText |> Hash.sha256 |> Hash.short}.fsx""")

        printfn $"Shadowing file %s{originalFilePath} to %s{newPath}"
        File.Copy(path, newPath)
        newPath, true

    if
      args.Contains No_Cache
      || Environment.GetEnvironmentVariable "FSY_NO_CACHE"
         |> Option.ofObj
         |> Option.contains "1"
    then

      let cacheDir = Path.Combine(cacheDirRoot, newFilePath |> Hash.sha256 |> Hash.short)

      if Directory.Exists cacheDir then
        if verbose then
          printfn $"Deleting directory %s{cacheDir} recursively..."

        Directory.Delete(cacheDir, true)

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

  match cmd.GetSubCommand() with
  | Run args ->
    let scriptFullPath = args |> getScript
    let output = compileScript args scriptFullPath
    output.Assembly.Value.EntryPoint.Invoke(null, Array.empty) |> ignore
  | Version -> printfn $"fsy %s{version}+%s{sha}"
  | Install_Fsx_Extensions args ->

    installFsxExtensions
      (args.TryGetResult Target_Dir)
      (match args.TryGetResult Framework_Version with
       | Some Net9 -> Some "net9.0"
       | _ -> None)
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

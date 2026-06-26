open Argu
open Queil.FSharp.FscHost
open Queil.FSharp.Hashing
open System.IO
open Fsy.Cli
open System
open System.Reflection
open System.Diagnostics
open System.Runtime.InteropServices

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

let acquireRestoreLock (lockDir: string) (timeout: TimeSpan) (log: string -> unit) : Async<IDisposable> =
  Directory.CreateDirectory lockDir |> ignore
  let lockFilePath = Path.Combine(lockDir, ".fsy-restore.lock")
  let sw = Stopwatch.StartNew()
  let staleAfter = timeout + TimeSpan.FromMinutes 1.0
  let mutable stream = None

  let deleteLock () =
    try
      File.Delete lockFilePath
    with :? IOException ->
      ()

  // best-effort release on Ctrl+C / SIGTERM — process teardown skips finally/Dispose.
  // guarded on stream.IsSome so we never delete a lock we don't own; default termination proceeds.
  let onSignal (ctx: PosixSignalContext) =
    if stream.IsSome then
      log $"Signal {ctx.Signal} — releasing lock: {lockFilePath}"
      deleteLock ()

  let mutable signals: IDisposable list = []

  let tryBreakStale () =
    try
      let fi = FileInfo lockFilePath

      if fi.Exists && DateTime.UtcNow - fi.LastWriteTimeUtc > staleAfter then
        log $"Breaking stale lock {lockFilePath} (age {DateTime.UtcNow - fi.LastWriteTimeUtc})"
        File.Delete lockFilePath
    with :? IOException ->
      ()

  async {
    while stream.IsNone && sw.Elapsed < timeout do
      try
        log $"Trying acquire lock on {lockFilePath}"
        // O_CREAT|O_EXCL — atomic across NFS/CSI shared volumes, unlike FileShare modes
        let fs =
          new FileStream(lockFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

        let bytes =
          Text.Encoding.UTF8.GetBytes($"{Environment.MachineName}:{Process.GetCurrentProcess().Id}")

        do! fs.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
        do! fs.FlushAsync() |> Async.AwaitTask
        stream <- Some fs

        signals <-
          [ PosixSignal.SIGINT; PosixSignal.SIGTERM; PosixSignal.SIGQUIT ]
          |> List.map (fun s -> PosixSignalRegistration.Create(s, Action<_> onSignal) :> IDisposable)

        log $"Acquired lock on: {lockFilePath}"
      with :? IOException ->
        log $"Waiting to acquire lock on {lockFilePath}"
        tryBreakStale ()
        do! Async.Sleep 1000

    match stream with
    | Some fs ->
      return
        { new IDisposable with
            member _.Dispose() =
              signals |> List.iter _.Dispose()
              fs.Dispose()

              try
                log $"Releasing lock: {lockFilePath}"
                deleteLock ()
                log $"Lock released: {lockFilePath}"
              with :? IOException as x ->
                log $"Failed releasing lock: {lockFilePath}\n%s{x.ToString()}"
                () }
    | None -> return raise (TimeoutException $"Could not acquire lock on {lockFilePath} within {timeout}")
  }

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
        let dir = Path.Combine(toolsDir, requested, "any")

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

  let compileWith
    (symbols: string list)
    (cacheDirOpt: string option)
    (noCache: bool)
    (shadowDirOpt: string option)
    (originalFilePath: string)
    (cont: CompileOutput -> unit)
    =
    let compilerOptions =
      { CompilerOptions.Default with
          IncludeHostEntryAssembly = false
          Target = "exe"
          Standalone = false
          LangVersion = Some "preview"
          Symbols = symbols
          Args =
            fun scriptPath refs opts ->
              [ "--noframework"
                "--nowin32manifest"
                yield! CompilerOptions.Default.Args scriptPath refs opts ] }

    let cacheDirRoot =
      cacheDirOpt
      |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "FSY_CACHE_DIR" |> Option.ofObj)
      |> Option.defaultValue (Path.GetTempPath())
      |> fun p -> Path.Combine(p, ".fsy", $"%s{version}+%s{sha[..7]}")
      |> Path.GetFullPath

    let options =
      { Options.Default with
          Compiler = compilerOptions
          Verbose = verbose
          Logger = if verbose then Some(fun msg -> printfn $"{msg}") else None
          AutoLoadNugetReferences = cmd.Contains Run || cmd.Contains Build
          UseCache = true }
      |> fun opts -> { opts with OutputDir = cacheDirRoot }

    let newFilePath, movedWithExtension =
      match originalFilePath with
      | path when path |> Path.HasExtension -> path, false
      | path ->
        let newPath =
          let scriptDir =
            shadowDirOpt
            |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "FSY_SHADOW_DIR" |> Option.ofObj)
            |> Option.map (Directory.CreateDirectory >> _.FullName)
            |> Option.defaultValue (Path.GetDirectoryName path)

          Path.Combine(scriptDir, $"""{path |> File.ReadAllText |> Hash.sha256 |> Hash.short}.fsx""")

        printfn $"Shadowing file %s{originalFilePath} to %s{newPath}"
        File.Copy(path, newPath)
        newPath, true

    try
      let sw = Stopwatch.StartNew()

      let lockDir =
        Environment.GetEnvironmentVariable "FSY_LOCK_DIR"
        |> Option.ofObj
        |> Option.defaultValue (
          let settings = NuGet.Configuration.Settings.LoadDefaultSettings null
          NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder settings
        )

      let log = if verbose then printfn "%s" else ignore

      let output =
        async {
          use! _ = acquireRestoreLock lockDir (TimeSpan.FromMinutes 5.0) log

          if
            noCache
            || Environment.GetEnvironmentVariable "FSY_NO_CACHE"
               |> Option.ofObj
               |> Option.contains "1"
          then
            let cacheDir = Path.Combine(cacheDirRoot, newFilePath |> Hash.sha256 |> Hash.short)

            if Directory.Exists cacheDir then
              if verbose then
                printfn $"Deleting directory %s{cacheDir} recursively..."

              Directory.Delete(cacheDir, true)

          return! CompilerHost.getAssembly options (Queil.FSharp.FscHost.File newFilePath)
        }
        |> Async.RunSynchronously

      if verbose then
        printfn $"fsch: {sw.ElapsedMilliseconds} ms"

      cont output
    finally
      if movedWithExtension then
        if verbose then
          printfn $"Deleting shadowed file: {newFilePath}"

        File.Delete newFilePath

  let runScript (args: ParseResults<ScriptArgs>) (originalFilePath: string) =
    compileWith
      (args.GetResults ScriptArgs.Symbol)
      (args.TryGetResult ScriptArgs.Cache_Dir)
      (args.Contains ScriptArgs.No_Cache)
      (args.TryGetResult ScriptArgs.Shadow_Dir)
      originalFilePath
      (fun output -> output.Assembly.Value.EntryPoint.Invoke(null, Array.empty) |> ignore)

  // Lays out a runnable, framework-dependent app next to the cached assembly:
  // the compiled script, its NuGet dependencies, FSharp.Core, and the
  // runtimeconfig.json the dotnet host needs. Run with: dotnet <output>/<name>.dll
  let buildArtifacts (output: CompileOutput) (scriptPath: string) (outputDir: string) =
    Directory.CreateDirectory outputDir |> ignore
    let appName = Path.GetFileNameWithoutExtension scriptPath

    let appDll = Path.Combine(outputDir, $"{appName}.dll")
    File.Copy(output.AssemblyFilePath, appDll, true)
    printfn $"Copied assembly:   %s{appDll}"

    let depsFile =
      Path.Combine(Path.GetDirectoryName output.AssemblyFilePath, "fsch.deps")

    let nugetDlls =
      if File.Exists depsFile then
        File.ReadAllLines depsFile
        |> Array.filter (fun l -> l.StartsWith "n#")
        |> Array.map (fun l -> l.Substring 2)
      else
        [||]

    let copyDep (srcDll: string) =
      let dst = Path.Combine(outputDir, Path.GetFileName srcDll)
      File.Copy(srcDll, dst, true)
      printfn $"Copied dependency: %s{dst}"

    nugetDlls |> Array.iter copyDep

    let fsharpCore = typeof<int list>.Assembly.Location

    if
      nugetDlls
      |> Array.exists (fun d -> Path.GetFileName d = Path.GetFileName fsharpCore)
      |> not
    then
      copyDep fsharpCore

    let v = Environment.Version

    let runtimeConfig =
      $$"""{
  "runtimeOptions": {
    "tfm": "net{{v.Major}}.0",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "{{v.Major}}.{{v.Minor}}.0"
    },
    "rollForward": "latestMinor"
  }
}
"""

    let rcPath = Path.Combine(outputDir, $"{appName}.runtimeconfig.json")
    File.WriteAllText(rcPath, runtimeConfig)
    printfn $"Wrote runtimeconfig: %s{rcPath}"
    printfn ""
    printfn $"Done. Run it with:  dotnet %s{appDll}"

  let buildScript (args: ParseResults<BuildArgs>) (originalFilePath: string) =
    let outputDir = args.GetResult BuildArgs.Output_Dir |> Path.GetFullPath

    compileWith
      (args.GetResults BuildArgs.Symbol)
      (args.TryGetResult BuildArgs.Cache_Dir)
      (args.Contains BuildArgs.No_Cache)
      (args.TryGetResult BuildArgs.Shadow_Dir)
      originalFilePath
      (fun output -> buildArtifacts output originalFilePath outputDir)

  let getScript (args: ParseResults<ScriptArgs>) =
    Path.GetFullPath(args.GetResult ScriptArgs.Script)

  match cmd.GetSubCommand() with
  | Run args ->
    let scriptFullPath = args |> getScript
    runScript args scriptFullPath

  | Build args ->
    let scriptFullPath = Path.GetFullPath(args.GetResult BuildArgs.Script)
    buildScript args scriptFullPath

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

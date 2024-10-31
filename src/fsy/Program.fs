open System.Security.Cryptography
open System.Text
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

[<RequireQualifiedAccess>]
module private Hash =
  let sha256 (s: string) =
    use sha256 = SHA256.Create()

    s
    |> Encoding.UTF8.GetBytes
    |> sha256.ComputeHash
    |> BitConverter.ToString
    |> _.Replace("-", "")

  let short (s: string) = s[0..10].ToLowerInvariant()

let sw = Stopwatch.StartNew()

try

  let rawCmd = Environment.GetCommandLineArgs() |> Seq.toList |> (fun l -> l[1..])
  let indexOfDoubleDash = rawCmd |> List.tryFindIndex (fun f -> f = "--")

  let (fsyArgs, passThruArgs) =
    match indexOfDoubleDash with
    | Some idx ->
      let fsyArgs, scriptArgs = rawCmd |> List.splitAt idx
      fsyArgs, scriptArgs[1..]
    | None -> rawCmd |> Seq.toList, []

  let parser = ArgumentParser.Create<Args>()

  let cmd = parser.Parse(fsyArgs |> Seq.toArray)
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

  let compileScript (args: ParseResults<ScriptArgs>) (filePath: string) =
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

    let cacheDirOverride = args.TryGetResult Cache_Dir |> Option.map (Path.GetFullPath)

    if args.Contains Force then

      match cacheDirOverride with
      | Some(cacheDir) ->
        if Directory.Exists cacheDir then
          if verbose then
            printfn $"Deleting directory %s{cacheDir} recursively..."

          Directory.Delete(cacheDir, true)
      | None ->
        ()

        let fschDir =
          Path.Combine(Path.GetTempPath(), ".fsch", File.ReadAllText filePath |> Hash.sha256 |> Hash.short)

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
          UseCache = true }
      |> fun opts ->
        match cacheDirOverride with
        | Some cacheDir -> { opts with OutputDir = cacheDir }
        | None -> opts

    let scriptPath, shadowCopyWithExtension =
      match filePath with
      | path when path |> Path.HasExtension -> path, false
      | path ->
        let newPath =
          Path.ChangeExtension(path, $"""{Guid.NewGuid().ToString("n")[..10]}.fsx""")

        File.Copy(path, newPath)
        newPath, true

    let beforeCompile = sw.ElapsedMilliseconds

    try
      let output =
        CompilerHost.getAssembly options (Queil.FSharp.FscHost.File scriptPath)
        |> Async.RunSynchronously

      if verbose then
        printfn $"fsch: {sw.ElapsedMilliseconds - beforeCompile} ms"

      output
    finally
      if shadowCopyWithExtension then
        if verbose then
          printfn $"Deleting shadowed file: {scriptPath}"

        File.Delete scriptPath

  let getScript (args: ParseResults<ScriptArgs>) = Path.GetFullPath(args.GetResult Script)

  let compile args =
    let script = args |> getScript
    let output = compileScript args script
    let defaultOutDir = Path.GetFileNameWithoutExtension(script)
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
| :? Argu.ArguParseException as exn -> printfn "%s" exn.Message
| :? ScriptCompileError as exn ->
  use _ =
    { new IDisposable with
        member _.Dispose() = Console.ResetColor() }

  Console.ForegroundColor <- ConsoleColor.Red
  exn.Diagnostics |> Seq.iter (System.Console.Error.WriteLine)
| :? FileNotFoundException as exn ->
  use _ =
    { new IDisposable with
        member _.Dispose() = Console.ResetColor() }

  Console.ForegroundColor <- ConsoleColor.Red
  $"ERROR: {exn.Message}" |> System.Console.Error.WriteLine
| :? TargetInvocationException as exn ->
  use _ =
    { new IDisposable with
        member _.Dispose() = Console.ResetColor() }

  Console.ForegroundColor <- ConsoleColor.Red
  $"ERROR: {exn.InnerException.Message}" |> System.Console.Error.WriteLine

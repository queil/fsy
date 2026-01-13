namespace Fsy.Cli

open Argu

type ScriptArgs =
  | [<AltCommandLine("-c"); Inherit>] Cache_Dir of string
  | [<AltCommandLine("-o"); Inherit>] Output_Dir of string
  | [<AltCommandLine("-f"); Inherit>] Force
  | [<AltCommandLine("-s"); Inherit >] Symbol of string
  | [<Last; CliPrefix(CliPrefix.None); MainCommand>] Script of ``script.fsx``: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Script _ -> "Path of the F# script to run/compile"
      | Cache_Dir _ -> "Sets the cache directory. Default: ./.fsy"
      | Output_Dir _ -> "Output dir. Default: a new dir created in cwd (named after the input script file name)"
      | Force -> "Clears the cache and forces re-compilation"
      | Symbol _ -> "Allows defining symbols that can be used e.g. in #if directives. Use multiple times to define many symbols"

type Args =
  | [<AltCommandLine("-v"); Inherit>] Verbose
  | [<CliPrefix(CliPrefix.None); SubCommand; AltCommandLine("ifsx")>] Install_Fsx_Extensions
  | [<CliPrefix(CliPrefix.None)>] Run of ParseResults<ScriptArgs>
  | [<CliPrefix(CliPrefix.None)>] Compile of ParseResults<ScriptArgs>

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Run _ -> "Runs the script in-process"
      | Compile _ -> "Compiles the script"
      | Install_Fsx_Extensions ->
        "Copies the dlls required for editor support to a stable location: ~/.fsharp/fsx-extensions/.fsch"
      | Verbose -> "Shows some log messages"

  static member FromCmdLine() =
    let argv = System.Environment.GetCommandLineArgs()[1..]
    let parser = ArgumentParser.Create<Args>(programName = "fsy")
    parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

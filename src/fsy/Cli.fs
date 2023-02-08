namespace Fsy.Cli

open Argu

type Args =
  | [<AltCommandLine("-c"); Inherit>] Cache_Dir of string
  | [<AltCommandLine("-o"); Inherit>] Output_Dir of string
  | [<AltCommandLine("-f"); Inherit>] Force
  | [<AltCommandLine("-v"); Inherit>] Verbose
  | [<AltCommandLine("-r")>] Run
  | [<Last; MainCommand; CliPrefix(CliPrefix.None)>] Script of ``script.fsx``:string
  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Script _ -> "Compiles an F# script and publishes the dll"
      | Run _ -> "Runs the script"
      | Cache_Dir _ -> "Sets the cache directory. Default: ./.fsy"
      | Output_Dir _ -> "Output dir. Default: a new dir created in cwd (named after the input script file name)"
      | Force -> "Clears the cache and forces re-compilation"
      | Verbose -> "Shows some log messages"

  static member FromCmdLine() =
    let argv = System.Environment.GetCommandLineArgs()[1..]
    let parser = ArgumentParser.Create<Args>(programName = "fsy")
    parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

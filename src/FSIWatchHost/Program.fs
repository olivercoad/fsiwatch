open System.IO
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading
open System.Globalization
open Microsoft.DotNet.Watcher
open Microsoft.DotNet.Watcher.Internal
open Microsoft.Extensions.Tools.Internal
open System.Threading.Tasks


let loadRegex = """^\s*#load\s+\"+(.*\.fsx?)\"+"""
let rRegex = """^\s*#r\s+\"+.*\"+"""

let regexOptions =
    RegexOptions.Compiled
    &&& RegexOptions.IgnoreCase
    &&& RegexOptions.Singleline

type ReferenceLine = ReferenceLine of string

type DependencyKind =
    | FileLoad of string
    | Reference of ReferenceLine

// matches a #load line and returns the loaded f# file path, combined with sourceFile location
let dependencyFromSourceLine (sourceFile: string) (line: string) =
    if not (Regex.IsMatch(line, "^\s*#")) then
        None
    elif Regex.IsMatch(line, rRegex) then
        Some(Reference(ReferenceLine line))
    else
        let m1 =
            Regex.Match(line, loadRegex, regexOptions)

        if m1.Success then
            let loadFile = m1.Groups.[1].Value

            let relativeToScript =
                Path.Combine(Path.GetDirectoryName(sourceFile), loadFile)

            Some(FileLoad relativeToScript)
        else
            None

let getDependencies (reporter: IReporter) (filePath) =
    let fileSet = HashSet()
    let referenceLines = ResizeArray() // keep correct order of references
    let mutable didError = false

    let rec addDependencies dependency =
        match dependency with
        | Reference r -> referenceLines.Add(r)
        | FileLoad filePath ->
            let filePath = Path.GetFullPath filePath

            if fileSet.Add filePath && File.Exists filePath then // HashSet.Add returns false if already in the set
                try
                    File.ReadLines filePath
                    |> Seq.choose (dependencyFromSourceLine filePath)
                    |> Seq.iter addDependencies
                with
                | :? IOException as e ->
                    didError <- true

                    reporter.Error
                    <| sprintf "Failed getting referenced files: %s" e.Message

    addDependencies (FileLoad filePath)
    (fileSet, referenceLines, didError)





let watchScript usePollingWatcher reporter mainFile (cancellationToken: CancellationToken) =

    let processSpec = ProcessSpec()
    let processRunnner = ProcessRunner(reporter)
    processSpec.Executable <- "dotnet"
    processSpec.EnvironmentVariables.["FSIWATCH"] <- "1"
    processSpec.Arguments <- [ "fsi"; mainFile ]

    let cancelledTaskSource = TaskCompletionSource()
    do ignore (cancellationToken.Register(fun () -> (cancelledTaskSource.TrySetResult() |> ignore)))

    let rec watchLoop iteration (prevDependencies: string seq) =
        async {
            processSpec.EnvironmentVariables.["FSIWATCH_ITERATION"] <-
                (iteration + 1)
                    .ToString(CultureInfo.InvariantCulture)


            let fileSet, _, didError = getDependencies reporter mainFile

            // If there was an error getting dependencies, then merge with
            // dependencies found in previous iteration. This way we pretect against dropping
            // dependencies from the watch fileset if there are transient IO errors.
            if didError then
                fileSet.UnionWith prevDependencies

            use processCTS =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

            use fileSetWatcher =
                new FileSetWatcher(fileSet, reporter, usePollingWatcher)


            if reporter.IsVerbose then
                let args =
                    System.String.Join(" ", processSpec.Arguments)

                reporter.Verbose
                <| sprintf "Running %s with the following arguments: %s" processSpec.Executable args

            let processTask =
                processRunnner.RunAsync(processSpec, processCTS.Token)

            reporter.Output "Started"

            let fileSetTask =
                fileSetWatcher.GetChangedFileAsync(cancellationToken)

            let! finishedTask =
                Task.WhenAny(processTask, fileSetTask, cancelledTaskSource.Task)
                |> Async.AwaitTask

            // Regardless of the which task finished first, make sure the process is cancelled
            // and wait for dotnet to exit. We don't want orphan processes
            processCTS.Cancel()

            let! exitCode = Async.AwaitTask(processTask)

            if (exitCode <> 0
                && finishedTask = (processTask :> Task)
                && not cancellationToken.IsCancellationRequested) then

                // Only show this error message if the process exited non-zero due to a normal process exit.
                // Don't show this if dotnet-watch killed the inner process due to file change or CTRL+C by the user
                reporter.Error $"Exited with error code {processTask.Result}"

            else
                reporter.Output "Exited"

            if not cancellationToken.IsCancellationRequested then
                if not fileSetTask.IsCompleted then
                    reporter.Warn "Waiting for a file to change before restarting dotnet..."

                let! _ = Async.AwaitTask fileSetTask
                return! watchLoop (iteration + 1) fileSet // hopefully tail call is supported
        }

    watchLoop 0 []


// let watchScriptFast usePollingWatcher reporter mainFile (cancellationToken: CancellationToken) =
//     let setupChildProc stuff =
//         // use anonymous pipes to communicate
//         // https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-use-anonymous-pipes-for-local-interprocess-communication
//         ()

//     let rec useNewProc stuff =
//         // do stuff then watchForChange
//         ()

//     and useStandbyProc stuff =
//         // do stuff then watchForChange
//         ()

//     and watchForChange stuff =
//         // if newReferences = standbyReferences
//         // then useStandbyProc
//         // else useNewProc
//         ()

//     // start with useNewProc
//     ()

[<EntryPoint>]
let main argv =
    match argv with
    | [| mainFile |] ->
        let usePollingWatcher = false

        let reporter =
            PrefixConsoleReporter("fsiwatch: ", PhysicalConsole.Singleton, true, false)

        let _cts = new CancellationTokenSource()

        System.Console.CancelKeyPress.Add
            (fun args ->
                args.Cancel <- not _cts.IsCancellationRequested

                if args.Cancel then
                    reporter.Output("Shutdown requested. Press Ctrl+C again to force exit.")

                _cts.Cancel())

        watchScript usePollingWatcher reporter mainFile _cts.Token
        |> Async.RunSynchronously

        0
    | [| "--fast"; mainFile |] ->
        printfn "Fast mode not implemented"
        1
    | _ ->
        printfn """Usage: "fsiwatch mainscript.fsx" or "fsiwatch --fast mainscript.fsx"."""
        1

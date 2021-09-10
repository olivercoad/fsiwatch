open System.IO
open System.Text.RegularExpressions
open System.Linq
open System.Collections.Generic
open System.Threading
open System.Globalization
open Microsoft.DotNet.Watcher
open Microsoft.DotNet.Watcher.Internal
open Microsoft.Extensions.Tools.Internal
open System.Threading.Tasks
open System.IO.Pipes
open System.IO
open System.IO
open System


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

type StandbyProcessStatus =
    | SwitchedToMain
    | Abandoned

let startStandbyProcess
    (reporter: IReporter)
    (processRunner: ProcessRunner)
    runnerDll
    references
    mainFile
    iteration
    cancellationToken
    (startSignalTask: Task)
    =
    async {

        let processSpec = ProcessSpec()
        processSpec.Executable <- "dotnet"
        processSpec.EnvironmentVariables.["FSIWATCH"] <- "1"

        processSpec.EnvironmentVariables.["FSIWATCH_ITERATION"] <-
            (iteration + 1)
                .ToString(CultureInfo.InvariantCulture)

        use pipeServerOut =
            new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)

        use pipeServerIn =
            new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)

        let clientHandleServerOut = pipeServerOut.GetClientHandleAsString()
        let clientHandleServerIn = pipeServerIn.GetClientHandleAsString()

        processSpec.Arguments <-
            [ runnerDll
              "--pipeClientHandles"
              clientHandleServerOut
              clientHandleServerIn
              "--mainFile"
              mainFile ]

        if reporter.IsVerbose then
            let args =
                System.String.Join(" ", processSpec.Arguments)

            reporter.Verbose
            <| sprintf "Running %s with the following arguments: %s" processSpec.Executable args


        let processTask =
            processRunner.RunAsync(processSpec, cancellationToken)

        pipeServerOut.DisposeLocalCopyOfClientHandle()
        pipeServerIn.DisposeLocalCopyOfClientHandle()

        let! didGetSwitchedToMain =
            async {
                try
                    use sw = new StreamWriter(pipeServerOut)
                    use sr = new StreamReader(pipeServerIn)
                    sw.AutoFlush <- true

                    let rec waitForMessage msg =
                        async {
                            let! line = Async.AwaitTask <| sr.ReadLineAsync()

                            if line <> msg then
                                if isNull line then
                                    raise (IOException "Unexpected EOF from standby process pipe")
                                else
                                    reporter.Verbose("(standby fsi) " + line)
                                    return! waitForMessage msg
                        }

                    let sendMessage (msg: string) =
                        async {
                            do! Async.AwaitTask(sw.WriteLineAsync(msg))
                            pipeServerOut.WaitForPipeDrain()
                        }

                    for ReferenceLine reference in references do
                        do! sendMessage (FSIWatchRunner.preloadReferencePrefix + reference)

                    do! sendMessage FSIWatchRunner.finishedSendingPreloads

                    let switchToMain () =
                        async {
                            do! sendMessage FSIWatchRunner.startSignal
                            do! waitForMessage null // null for EOF of stream. the runner will close pipe after it receives the signal
                            sr.Close()
                            sw.Close() //explicitly close to send EOF so that runner knows it can continue
                            return true // if the process exits before here, the pipe will get broken and cause IOException that will get caught
                        }

                    if startSignalTask.IsCompletedSuccessfully then
                        // send signal to switch to main immediately so that it runs preloads in foreground
                        return! switchToMain ()

                    else
                        // have runner execute preloads in background and then wait for signal
                        do! sendMessage FSIWatchRunner.runPreloadsThenWait

                        do! waitForMessage FSIWatchRunner.hasRunPreloads
                        // wait till either process ends or ready to send signal
                        let! finishedTask =
                            Async.AwaitTask
                            <| Task.WhenAny(processTask, startSignalTask)

                        if finishedTask = startSignalTask
                           && not cancellationToken.IsCancellationRequested then

                            if iteration <> 0 then
                                reporter.Output("Signalling standby process to start running script")

                            return! switchToMain ()
                        else
                            // process exited unexpectedly or cancelled before the signal to switch to main
                            return false

                with
                | :? IOException as e ->
                    // pipe was probably broken by process exiting
                    if not cancellationToken.IsCancellationRequested then
                        reporter.Error(sprintf "Standby process error: %A" e)

                    return false
            }

        return processTask, didGetSwitchedToMain
    }

let arePreloadedReferencesChanged prevReferences references =
    // adding new references to the end is okay, but changing or removing references is not.
    if prevReferences = references then
        false
    elif List.length references < List.length prevReferences then
        true // prevReferences was longer, so some have been removed
    else
        let commonLen =
            List.take (List.length prevReferences) references

        List.exists2 (<>) commonLen prevReferences // true if any are different

let watchScriptFast usePollingWatcher reporter mainFile (cancellationToken: CancellationToken) =

    let processRunner = ProcessRunner(reporter)
    let runnerDll = FSIWatchRunner.getAssemblyLocation ()

    System.Diagnostics.Debug.Assert(
        Path.GetFileName(runnerDll) = "FSIWatchRunner.dll",
        $"Invalid fsiwatch runner dll: %s{runnerDll}"
    )

    let cancelledTaskSource = TaskCompletionSource()
    do ignore (cancellationToken.Register(fun () -> (cancelledTaskSource.TrySetResult() |> ignore)))


    let rec watchLoop
        iteration
        (prevFileSet: string seq)
        prevReferences
        (standbyRunnerTaskAsync: Async<Task<int> * bool>)
        (standbyRunnerTCS: TaskCompletionSource)
        (standbyRunnerCTS: CancellationTokenSource)
        =
        async {
            let fileSet, references, didError =
                getDependencies (reporter: IReporter) mainFile

            if didError then
                // If there was an error getting dependencies, then merge with
                // dependencies found in previous iteration. This way we pretect against dropping
                // dependencies from the watch fileset if there are transient IO errors.
                fileSet.UnionWith prevFileSet

                // Use the previous references loop. It's no biggie if we don't preload new references.
                references.Clear()
                // This doesn't handle if reference order is changed or references are removed.
                references.AddRange(prevReferences)

            let references = List.ofSeq references

            if arePreloadedReferencesChanged prevReferences references then
                // ditch standby process and start from scratch
                reporter.Error "References changed; abandonning standby fsi runner"
                standbyRunnerCTS.Cancel()

                let! standbyRunnerTask, _ = standbyRunnerTaskAsync
                do! Async.AwaitTask(standbyRunnerTask :> Task)

            use fileSetWatcher =
                new FileSetWatcher(fileSet, reporter, usePollingWatcher)

            // assume that the process is already running in standby (check didSwitchToMain later)
            standbyRunnerTCS.SetResult() // signal standby runner to switch to main script
            let! standbyProcessTask, didSwitchToMain = standbyRunnerTaskAsync

            let! processTask, processCTS =
                async {
                    if didSwitchToMain then
                        return (standbyProcessTask, standbyRunnerCTS)
                    else
                        // the standby process failed to switch to main, so kill it then start a new one that switches immediately.
                        if iteration > 0 then // this is normal as first iteration has dummy standby tasks
                            reporter.Error(
                                "Standby process was abandonned or failed before running main script, so starting a new one."
                            )

                        standbyRunnerCTS.Cancel() // make sure the standby process is killed
                        do! Async.AwaitTask(standbyProcessTask :> Task)

                        let cts =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                        let! newStandbyTaskAsync =
                            Async.StartChild(
                                startStandbyProcess
                                    reporter
                                    processRunner
                                    runnerDll
                                    references
                                    mainFile
                                    iteration
                                    cts.Token
                                    Task.CompletedTask
                            )

                        let! t, _ = newStandbyTaskAsync
                        return (t, cts)
                }


            let newStandbyTCS = TaskCompletionSource()

            let newStandbyCTS =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

            let! newStandbyTaskAsync =
                Async.StartChild(
                    startStandbyProcess
                        reporter
                        processRunner
                        runnerDll
                        references
                        mainFile
                        iteration
                        newStandbyCTS.Token
                        newStandbyTCS.Task
                )

            if didSwitchToMain then
                reporter.Output "Started"

            let fileSetTask = fileSetWatcher.GetChangedFileAsync(cancellationToken)

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
                let isWaitingOnFileset = not fileSetTask.IsCompleted
                if isWaitingOnFileset then
                    reporter.Warn "Waiting for a file to change before restarting dotnet..."

                let! _ = Async.AwaitTask fileSetTask

                if not cancellationToken.IsCancellationRequested then
                    if isWaitingOnFileset then
                        // important to wait for changes to be written to disk
                        // otherwise we get transient errors when reading dependencies, or it may
                        // even read empty. If you know a better way of mitigating these race conditions of
                        // waiting for file IO then please let me know :)
                        let throttleDelay = TimeSpan.FromMilliseconds 100.
                        do! Async.Sleep throttleDelay

                    // hopefully tail call is supported
                    return! watchLoop (iteration + 1) fileSet references newStandbyTaskAsync newStandbyTCS newStandbyCTS
        }

    let dummyTask =
        async { return Task.FromResult -1, false }

    let dummyCTS = new CancellationTokenSource()
    dummyCTS.Cancel()
    let dummyTCS = TaskCompletionSource()
    watchLoop 0 [] [] dummyTask dummyTCS dummyCTS



// let rec watchLoop iteration (prevDependencies: string seq) =
//     async {
//         processSpec.EnvironmentVariables.["FSIWATCH_ITERATION"] <-
//             (iteration + 1)
//                 .ToString(CultureInfo.InvariantCulture)


//         let fileSet, _, didError = getDependencies reporter mainFile

//         // If there was an error getting dependencies, then merge with
//         // dependencies found in previous iteration. This way we pretect against dropping
//         // dependencies from the watch fileset if there are transient IO errors.
//         if didError then
//             fileSet.UnionWith prevDependencies

//         use processCTS =
//             CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

//         use fileSetWatcher =
//             new FileSetWatcher(fileSet, reporter, usePollingWatcher)


//         if reporter.IsVerbose then
//             let args =
//                 System.String.Join(" ", processSpec.Arguments)

//             reporter.Verbose
//             <| sprintf "Running %s with the following arguments: %s" processSpec.Executable args

//         let processTask =
//             processRunnner.RunAsync(processSpec, processCTS.Token)

//         reporter.Output "Started"

//         let fileSetTask =
//             fileSetWatcher.GetChangedFileAsync(cancellationToken)

//         let! finishedTask =
//             Task.WhenAny(processTask, fileSetTask, cancelledTaskSource.Task)
//             |> Async.AwaitTask

//         // Regardless of the which task finished first, make sure the process is cancelled
//         // and wait for dotnet to exit. We don't want orphan processes
//         processCTS.Cancel()

//         let! exitCode = Async.AwaitTask(processTask)

//         if (exitCode <> 0
//             && finishedTask = (processTask :> Task)
//             && not cancellationToken.IsCancellationRequested) then

//             // Only show this error message if the process exited non-zero due to a normal process exit.
//             // Don't show this if dotnet-watch killed the inner process due to file change or CTRL+C by the user
//             reporter.Error $"Exited with error code {processTask.Result}"

//         else
//             reporter.Output "Exited"

//         if not cancellationToken.IsCancellationRequested then
//             if not fileSetTask.IsCompleted then
//                 reporter.Warn "Waiting for a file to change before restarting dotnet..."

//             let! _ = Async.AwaitTask fileSetTask
//             return! watchLoop (iteration + 1) fileSet // hopefully tail call is supported
//     }

// watchLoop 0 []


// let setupChildProc stuff =
//     // use anonymous pipes to communicate
//     // https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-use-anonymous-pipes-for-local-interprocess-communication
//     ()

// let rec useNewProc stuff =
//     // do stuff then watchForChange
//     ()

// and useStandbyProc stuff =
//     // do stuff then watchForChange
//     ()

// and watchForChange stuff =
//     // if newReferences = standbyReferences
//     // then useStandbyProc
//     // else useNewProc
//     ()

// // start with useNewProc
// ()

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
        let usePollingWatcher = false
        let verbose = false
        let quiet = false
        let reporter =
            PrefixConsoleReporter("fsiwatch: ", PhysicalConsole.Singleton, verbose, quiet)

        let _cts = new CancellationTokenSource()

        System.Console.CancelKeyPress.Add
            (fun args ->
                args.Cancel <- not _cts.IsCancellationRequested

                if args.Cancel then
                    reporter.Output("Shutdown requested. Press Ctrl+C again to force exit.")

                _cts.Cancel())

        watchScriptFast usePollingWatcher reporter mainFile _cts.Token
        |> Async.RunSynchronously

        0
    | _ ->
        printfn """Usage: "fsiwatch mainscript.fsx" or "fsiwatch --fast mainscript.fsx"."""
        1

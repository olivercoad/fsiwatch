module FSIWatchRunner
open System.Runtime.CompilerServices
open System.IO.Pipes
open System.IO

[<MethodImpl(MethodImplOptions.NoInlining)>]
let getAssemblyLocation () =
    System.Reflection.Assembly.GetExecutingAssembly().Location

let preloadReferencePrefix = "preload-reference: "
let finishedSendingPreloads = "finishedSendingPreloads"
let runPreloadsThenWait = "runPreloadsThenWait" // used to tell standby process to run preload lines then wait for the signal
let hasRunPreloads = "Has run preloads" // used to tell host process that the preloads have finished running
let startSignal = "startSignal" // used to tell standby process to start running as the main script now

let runPreloadLines lines =
    printfn "running preload lines"
    System.Threading.Tasks.Task.Delay(5000).Wait()
    printfn "Okay, loaded compiler, actually running preload lines now"

    for line in lines do
        printfn "Running line: %s" line

let preloadAndWaitForSignal (clientHandleClientIn:string) (clientHandleClientOut:string) =
    use pipeClientIn = new AnonymousPipeClientStream(PipeDirection.In, clientHandleClientIn)
    use pipeClientOut = new AnonymousPipeClientStream(PipeDirection.Out, clientHandleClientOut)
    use sr = new StreamReader(pipeClientIn)
    use sw = new StreamWriter(pipeClientOut)
    sw.AutoFlush <- true

    // send output back to parent through pipe until we get signalled to take over as the main script runner
    let savedConsoleOut = System.Console.Out
    let savedConsoleErr = System.Console.Out
    System.Console.SetOut(sw)
    System.Console.SetError(sw)

    let rec readPreloads lines =
        let line = sr.ReadLine()
        if isNull line then
            failwith "client pipe unexpected EOF"
        elif line = finishedSendingPreloads then
            printfn "Got preload lines"
            List.rev lines
        elif line.StartsWith(preloadReferencePrefix) then
            let preloadLine = line.Substring(preloadReferencePrefix.Length)
            readPreloads (preloadLine::lines)
        else
            failwithf "Unexpected message: %s" line
    
    printfn "Reading preload lines"
    let preloadLines = readPreloads []

    let nextAction = sr.ReadLine()

    let switchToMain () =
        pipeClientOut.WaitForPipeDrain()
        System.Console.SetOut(savedConsoleOut)
        System.Console.SetError(savedConsoleErr)
        sw.Close()
        sr.ReadToEnd() |> ignore // wait to allow host to close their end and log messages
        sr.Close()

    if nextAction = runPreloadsThenWait then
        printfn "Running preload lines while waiting for signal"
        runPreloadLines preloadLines

        printfn "Waiting for signal to start main script"
        printfn "%s" hasRunPreloads
        let line = sr.ReadLine()
        if line <> startSignal then
            failwithf "Unexpected message: %s" line
        printfn "Received signal, switching to main script"
        switchToMain()
    elif nextAction = startSignal then
        printfn "Received signal, switching to main immediately"
        switchToMain()
        runPreloadLines preloadLines

[<EntryPoint>]
let main argv =
    match argv with
    | [| "--pipeClientHandles"; clientHandleClientIn; clientHandleClientOut; "--mainFile"; mainscript |] ->
        preloadAndWaitForSignal clientHandleClientIn clientHandleClientOut
        printfn "running as main script now, woo0Oo!!!!"
        printfn "%s" (System.IO.File.ReadAllText(mainscript))
        System.Threading.Tasks.Task.Delay(5000).Wait()
        printfn "exiting the watch runner now, okay"
        0
    | _ ->
        printfn "please provide pipe client handles"
        printfn "arvg: %A" argv
        1
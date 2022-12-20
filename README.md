This is a tool for running f# .fsx scripts in watch mode.

# Usage

```
fsiwatch script.fsx
```

# Watched files

By default, fsiwatch will recursively look for `#load` directives to generate a list of dependant files that it will watch.
It will not watch referenced assemblies.

TODO: Add option for adding files/directories to be watched  
TODO: check env DOTNET_USE_POLLING_FILE_WATCHER to poll the filesystem

# Fast modes

⚠ This is a work in progress. Fast modes have not been fully implemented yet and do not work.

The default behaviour when a change is detected is to kill the process (if not already exited) and run the script again in a new process.
This is the most reliable option.

In order to speed up the restart time, a second process with a warmed up `FsiEvaluationSession` will be started, waiting
for the signal to run the script when it's time. While it's waiting, it will pre-load any `#r` references.


------------------------------

TODO:


### Fast

If you use the `--fast` option, rather than killing the entire process, it will just (attempt to) unload the dynamic assembly and create
a new `FsiEvaluationSession` in the same process.

It will also keep a fresh `FsiEvaluationSession` with `#r` references pre-loaded, ready for when the previous session is done.

Although it's more safer than `--faster`, things can still leak between sessions.
Even though it's a fresh evaluation session, it's not a fresh process. Mutable static things and singletons in other
assemblies won't get reset. Any new threads or async operations from the previous session could still be running.

### Faster

If you use the `--faster` option, when a change is detected fsiwatch will keep the state of the `FsiEvaluationSession`
but send the script for evaluation again.

This means that the state of the process will NOT be reset and it's possible for things to leak from the previous run.
This is similar to if you run "FSI: Send File" from VSCode multiple times.

### Fastest (Experimental)

The `--fastest` option is like `--faster`, except fsiwatch will try to intelligently keep track which file a change
was made in and only execute things from after the change.

For this to work, all `#load` lines are hoisted to the top of each file.


### Uberfast (Very Experimental)

Like `--fastest`, but also based on the line number.

It walks up the AST to the last point before the change that was at zero indentation, and sends from there.

//note to self - if we use directives like `# 1 @ "C:/.../loaded.fsx"`, will it automatically remember context like which namespaces
are open and top level module/namespace declarations? Or do we need to send them also?

# Signalling the script to stop

When a change is made, the current script must stopped before the next one can be run. If your script doesn't exit quickly,
the process will be killed so that the new session can run.

If you want to make best use of fast modes, your script needs to behave nicely and exit quickly when signalled (if it hasn't already finished).

If you're using any of the fast modes,

 - **EOF on stdin**: `Console.ReadLine()` will return `null`
 - **Disposable**: If the main script returns an `IDisposable` on the last line, it will be disposed.
 - **Async**: If you're script returns Async, it will be started in the threadpool with a CancellationToken
 - **CancellationToken**: There is a `CancellationToken` on the fsi object, `fsi.FSIWatchLifetime`
 - **Event**: There is an event on the fsi object, `fsi.FSIWatchRestart`


If you're running an infinite loop reading from stdin, just stop the looping if it's EOF.


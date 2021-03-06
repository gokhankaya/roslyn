﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CompilerServer;
using static Microsoft.CodeAnalysis.BuildTasks.NativeMethods;
using static Microsoft.CodeAnalysis.CompilerServer.BuildProtocolConstants;
using static Microsoft.CodeAnalysis.CompilerServer.CompilerServerLogger;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.BuildTasks
{
    /// <summary>
    /// Client class that handles communication to the server.
    /// </summary>
    internal static class BuildClient
    {
        private const string s_serverName = "VBCSCompiler.exe";
        // Spend up to 1s connecting to existing process (existing processes should be always responsive).
        private const int TimeOutMsExistingProcess = 1000;
        // Spend up to 20s connecting to a new process, to allow time for it to start.
        private const int TimeOutMsNewProcess = 20000; 

        /// <summary>
        /// Run a compilation through the compiler server and print the output
        /// to the console. If the compiler server fails, run the fallback
        /// compiler.
        /// </summary>
        public static int RunWithConsoleOutput(
            string[] args,
            string clientDir,
            string workingDir,
            string sdkDir,
            RequestLanguage language,
            Func<string, string, string[], int> fallbackCompiler)
        {
            args = args.Select(arg => arg.Trim()).ToArray();

            bool hasShared;
            string keepAlive;
            string errorMessage;
            List<string> parsedArgs;
            if (!CommandLineParser.TryParseClientArgs(
                    args,
                    out parsedArgs,
                    out hasShared,
                    out keepAlive,
                    out errorMessage))
            {
                Console.Out.WriteLine(errorMessage);
                return CommonCompiler.Failed;
            }

            if (hasShared)
            {
                var responseTask = TryRunServerCompilation(
                    language,
                    clientDir,
                    workingDir,
                    parsedArgs,
                    default(CancellationToken),
                    keepAlive: keepAlive,
                    libEnvVariable: Environment.GetEnvironmentVariable("LIB"));

                var response = responseTask.Result;
                if (response != null)
                {
                    return HandleResponse(response);
                }
            }

            return fallbackCompiler(clientDir, sdkDir, parsedArgs.ToArray());
        }

        private static int HandleResponse(BuildResponse response)
        {
            if (response.Type == BuildResponse.ResponseType.Completed)
            {
                var completedResponse = (CompletedBuildResponse)response;
                return ConsoleUtil.RunWithOutput(
                    completedResponse.Utf8Output,
                    (outWriter, errorWriter) =>
                    {
                        outWriter.Write(completedResponse.Output);
                        errorWriter.Write(completedResponse.ErrorOutput);
                        return completedResponse.ReturnCode;
                    });
            }
            else
            {
                Console.Error.WriteLine(CommandLineParser.MismatchedVersionErrorText);
                return CommonCompiler.Failed;
            }
        }

        /// <summary>
        /// Returns a Task with a null BuildResponse if no server
        /// response was received.
        /// </summary>
        public static Task<BuildResponse> TryRunServerCompilation(
            RequestLanguage language,
            string clientDir, 
            string workingDir,
            IList<string> arguments,
            CancellationToken cancellationToken,
            string keepAlive = null,
            string libEnvVariable = null)
        {
            try
            {
                NamedPipeClientStream pipe;

                if (clientDir == null)
                    return Task.FromResult<BuildResponse>(null);

                var pipeName = GetPipeName(clientDir);
                bool holdsMutex;
                using (var mutex = new Mutex(initiallyOwned: true,
                                             name: pipeName,
                                             createdNew: out holdsMutex))
                {
                    try
                    {

                        if (!holdsMutex)
                        {
                            try
                            {
                                holdsMutex = mutex.WaitOne(TimeOutMsNewProcess);
                            }
                            catch (AbandonedMutexException)
                            {
                                holdsMutex = true;
                            }
                        }

                        if (holdsMutex)
                        {
                            var request = BuildRequest.Create(language, workingDir, arguments, keepAlive, libEnvVariable);
                            // Check for already running processes in case someone came in before us
                            if (null != (pipe = TryConnectToProcess(pipeName,
                                                                    TimeOutMsExistingProcess,
                                                                    cancellationToken)))
                            {
                                return TryCompile(pipe, request, cancellationToken);
                            }
                            else
                            {
                                if (TryCreateServerProcess(clientDir, pipeName) &&
                                    null != (pipe = TryConnectToProcess(pipeName,
                                                                        TimeOutMsNewProcess,
                                                                        cancellationToken)))
                                {
                                    // Let everyone else access our process
                                    mutex.ReleaseMutex();
                                    holdsMutex = false;

                                    return TryCompile(pipe, request, cancellationToken);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (holdsMutex)
                            mutex.ReleaseMutex();
                    }
                }
            }
            // Swallow all unhandled exceptions from server compilation. If
            // they are show-stoppers then they will crash the in-proc
            // compilation as well
            // TODO: Put in non-fatal Watson code so we still get info
            // when things unexpectedely fail
            catch { }
            return Task.FromResult<BuildResponse>(null);
        }

        /// <summary>
        /// Try to compile using the server. Returns null if a response from the
        /// server cannot be retrieved.
        /// </summary>
        private static async Task<BuildResponse> TryCompile(NamedPipeClientStream pipeStream,
                                                            BuildRequest request,
                                                            CancellationToken cancellationToken)
        {
            BuildResponse response;
            using (pipeStream)
            {
                // Write the request
                try
                {
                    Log("Begin writing request");
                    await request.WriteAsync(pipeStream, cancellationToken).ConfigureAwait(false);
                    Log("End writing request");
                }
                catch (Exception e)
                {
                    LogException(e, "Error writing build request.");
                    return null;
                }

                // Wait for the compilation and a monitor to dectect if the server disconnects
                var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                Log("Begin reading response");

                var responseTask = BuildResponse.ReadAsync(pipeStream, serverCts.Token);
                var monitorTask = CreateMonitorDisconnectTask(pipeStream, serverCts.Token);
                await Task.WhenAny(responseTask, monitorTask).ConfigureAwait(false);

                Log("End reading response");

                if (responseTask.IsCompleted)
                {
                    // await the task to log any exceptions
                    try
                    {
                        response = await responseTask.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        LogException(e, "Error reading response");
                        response = null;
                    }
                }
                else
                {
                    Log("Server disconnect");
                    response = null;
                }

                // Cancel whatever task is still around
                serverCts.Cancel();
                return response;
            }
        }

        /// <summary>
        /// The IsConnected property on named pipes does not detect when the client has disconnected
        /// if we don't attempt any new I/O after the client disconnects. We start an async I/O here
        /// which serves to check the pipe for disconnection. 
        ///
        /// This will return true if the pipe was disconnected.
        /// </summary>
        private static async Task CreateMonitorDisconnectTask(
            NamedPipeClientStream pipeStream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[0];

            while (!cancellationToken.IsCancellationRequested && pipeStream.IsConnected)
            {
                // Wait a tenth of a second before trying again
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);

                try
                {
                    Log("Before poking pipe.");
                    await pipeStream.ReadAsync(buffer, 0, 0, cancellationToken).ConfigureAwait(false);
                    Log("After poking pipe.");
                }
                // Ignore cancellation
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    // It is okay for this call to fail.  Errors will be reflected in the 
                    // IsConnected property which will be read on the next iteration of the 
                    LogException(e, "Error poking pipe");
                }
            }
        }

        /// <summary>
        /// Get the file path of the executable that started this process.
        /// </summary>
        /// <param name="processHandle"></param>
        /// <param name="flags">Should always be 0: Win32 path format.</param>
        /// <param name="exeNameBuffer">Buffer for the name</param>
        /// <param name="bufferSize">
        /// Size of the buffer coming in, chars written coming out.
        /// </param>
        [DllImport("Kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(
            IntPtr processHandle,
            int flags,
            StringBuilder exeNameBuffer,
            ref int bufferSize);

        private const int MAX_PATH_SIZE = 260;

        /// <summary>
        /// Connect to the pipe for a given directory and return it.
        /// Throws on cancellation.
        /// </summary>
        /// <param name="pipeName">Name of the named pipe to connect to.</param>
        /// <param name="timeoutMs">Timeout to allow in connecting to process.</param>
        /// <param name="cancellationToken">Cancellation token to cancel connection to server.</param>
        /// <returns>
        /// An open <see cref="NamedPipeClientStream"/> to the server process or null on failure.
        /// </returns>
        private static NamedPipeClientStream TryConnectToProcess(
            string pipeName,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            NamedPipeClientStream pipeStream;
            try
            {
                // Machine-local named pipes are named "\\.\pipe\<pipename>".
                // We use the SHA1 of the directory the compiler exes live in as the pipe name.
                // The NamedPipeClientStream class handles the "\\.\pipe\" part for us.
                Log("Attempt to open named pipe '{0}'", pipeName);

                pipeStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                cancellationToken.ThrowIfCancellationRequested();

                Log("Attempt to connect named pipe '{0}'", pipeName);
                pipeStream.Connect(timeoutMs);
                Log("Named pipe '{0}' connected", pipeName);

                cancellationToken.ThrowIfCancellationRequested();

                /*
                    no security API on CoreCLR 

                // Verify that we own the pipe.
                SecurityIdentifier currentIdentity = WindowsIdentity.GetCurrent().Owner;
                PipeSecurity remoteSecurity = pipeStream.GetAccessControl();
                IdentityReference remoteOwner = remoteSecurity.GetOwner(typeof(SecurityIdentifier));
                if (remoteOwner != currentIdentity)
                {
                    Log("Owner of named pipe is incorrect");
                    return null;
                }
                */

                return pipeStream;
            }
            catch (Exception e) when (!(e is TaskCanceledException))
            {
                LogException(e, "Exception while connecting to process");
                return null;
            }
        }

        /// <summary>
        /// Create a new instance of the server process, returning true on success
        /// and false otherwise.
        /// </summary>
        private static bool TryCreateServerProcess(string clientDir, string pipeName)
        {
            // The server should be in the same directory as the client
            string expectedPath = Path.Combine(clientDir, s_serverName);

            if (!File.Exists(expectedPath))
                return false;

            // As far as I can tell, there isn't a way to use the Process class to 
            // create a process with no stdin/stdout/stderr, so we use P/Invoke.
            // This code was taken from MSBuild task starting code.

            STARTUPINFO startInfo = new STARTUPINFO();
            startInfo.cb = Marshal.SizeOf(startInfo);
            startInfo.hStdError = InvalidIntPtr;
            startInfo.hStdInput = InvalidIntPtr;
            startInfo.hStdOutput = InvalidIntPtr;
            startInfo.dwFlags = STARTF_USESTDHANDLES;
            uint dwCreationFlags = NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW;

            PROCESS_INFORMATION processInfo;

            Log("Attempting to create process '{0}'", expectedPath);

            var builder = new StringBuilder($@"""{expectedPath}"" ""-pipename:{pipeName}""");

            bool success = CreateProcess(
                lpApplicationName:    null,
                lpCommandLine:        builder,
                lpProcessAttributes:  NullPtr,
                lpThreadAttributes:   NullPtr,
                bInheritHandles:      false,
                dwCreationFlags:      dwCreationFlags,
                lpEnvironment:        NullPtr, // Inherit environment
                lpCurrentDirectory:   clientDir,
                lpStartupInfo:        ref startInfo,
                lpProcessInformation: out processInfo);

            if (success)
            {
                Log("Successfully created process with process id {0}", processInfo.dwProcessId);
                CloseHandle(processInfo.hProcess);
                CloseHandle(processInfo.hThread);
            }
            else
            {
                Log("Failed to create process. GetLastError={0}", Marshal.GetLastWin32Error());
            }
            return success;
        }
    }
}

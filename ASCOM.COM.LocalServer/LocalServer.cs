//
// ASCOM.DynamicDemo.SafetyMonitor Local COM Server
//
// This is the core of a managed COM Local Server, capable of serving
// multiple instances of multiple interfaces, within a single
// executable. This implements the equivalent functionality of VB6
// which has been extensively used in ASCOM for drivers that provide
// multiple interfaces to multiple clients (e.g. Meade Telescope
// and Focuser) as well as hubs (e.g., POTH).
//
// Written by: Robert B. Denny (Version 1.0.1, 29-May-2007)
// Modified by Chris Rowland and Peter Simpson to allow use with multiple devices of the same type March 2011
//
//
using ASCOM;
using ASCOM.Com;
using ASCOM.Common;
using ASCOM.Common.DeviceInterfaces;
using ASCOM.Common.Interfaces;
using ASCOM.Tools;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ASCOM.LocalServer
{
    public class Server
    {

        #region Variables

        private static uint mainThreadId; // Stores the main thread's thread id.
        private static bool startedByCOM; // True if server started by COM (-embedding)
        private static int driversInUseCount; // Keeps a count on the total number of objects alive.
        private static int serverLockCount; // Keeps a lock count on this application.
        private static ArrayList driverTypes; // Served COM object types
        private static ArrayList classFactories; // Served COM object class factories
        private static string localServerAppId = "{aad1c35c-2bf8-4cf9-b0f2-4e13b2595bb6}"; // Our AppId
        private static readonly Object lockObject = new object(); // Counter lock object
        private static TraceLogger TL; // TraceLogger for the local server (not the served driver, which has its own) - primarily to help debug local server issues
        private static Task GCTask; // The garbage collection task
        private static CancellationTokenSource GCTokenSource; // Token source used to end periodic garbage collection.

        #endregion

        #region Local Server entry point (main)

        /// <summary>
        /// Main server entry point
        /// </summary>
        /// <param name="args">Command line parameters</param>
        [STAThread]
        static void Main(string[] args)
        {
            // Create a trace logger for the local server.
            TL = new TraceLogger("DynamicDemo.LocalServer", false)
            {
                Enabled = true // Enable to debug local server operation (not usually required). Drivers have their own independent trace loggers.
            };
            TL.LogMessage("Main", $"Server started");

            // Load driver COM assemblies and get types, ending the program if something goes wrong.
            TL.LogMessage("Main", $"Loading drivers");
            if (!PopulateListOfAscomDrivers()) return;

            // Process command line arguments e.g. to Register/Unregister drivers, ending the program if required.
            TL.LogMessage("Main", $"Processing command-line arguments");
            if (!ProcessArguments(args)) return;

            // Initialize variables.
            TL.LogMessage("Main", $"Initialising variables");
            driversInUseCount = 0;
            serverLockCount = 0;
            mainThreadId = GetCurrentThreadId();
            Thread.CurrentThread.Name = "DynamicDemo Local Server Thread";

            // Register the class factories of the served objects
            TL.LogMessage("Main", $"Registering class factories");
            RegisterClassFactories();

            // Start the garbage collection thread.
            TL.LogMessage("Main", $"Starting garbage collection");
            StartGarbageCollection(10000);
            TL.LogMessage("Main", $"Garbage collector thread started");

            // Start the message loop to serialize incoming calls to the served driver COM objects.
            try
            {
                TL.LogMessage("Main", $"Starting main form");
                ASCOM.Alpaca.Simulators.Program.Main(args);
                TL.LogMessage("Main", $"Main form has ended");
            }
            finally
            {
                // Revoke the class factories immediately without waiting until the thread has stopped
                TL.LogMessage("Main", $"Revoking class factories");
                RevokeClassFactories();
                TL.LogMessage("Main", $"Class factories revoked");

                // No new connections are now possible and the local server is irretrievably shutting down, so release resources in the Hardware classes
                try
                {
                    // Get all types in the local server assembly
                    Type[] types = Assembly.GetExecutingAssembly().GetTypes();

                    // Iterate over the types looking for hardware classes that need to be disposed
                    foreach (Type type in types)
                    {
                        try
                        {
                            TL.LogMessage("Main", $"Hardware disposal - Found type: {type.Name}");

                            // Get the HardwareClassAttribute attribute if present on this type
                            object[] attrbutes = type.GetCustomAttributes(typeof(HardwareClassAttribute), false);

                            // Check to see if this type has the HardwareClass attribute, which indicates that this is a hardware class.
                            if (attrbutes.Length > 0) // There is a HardwareClass attribute so call its Dispose() method
                            {
                                TL.LogMessage("Main", $"  {type.Name} is a hardware class");

                                // Only process static classes that don't have instances here.
                                if (type.IsAbstract & type.IsSealed) // This type is a static class
                                {
                                    // Lookup the method
                                    MethodInfo disposeMethod = type.GetMethod("Dispose");

                                    // If the method is found call it
                                    if (disposeMethod != null) // a public Dispose() method was found
                                    {
                                        TL.LogMessage("Main", $"  Calling method {disposeMethod.Name} in static class {type.Name}...");

                                        // Now call Dispose()
                                        disposeMethod.Invoke(null, null);
                                        TL.LogMessage("Main", $"  {disposeMethod.Name} method called OK.");
                                    }
                                    else // No public Dispose method was found
                                    {
                                        TL.LogMessage("Main", $"  The {disposeMethod.Name} method does not contain a public Dispose() method.");
                                    }
                                }
                                else
                                {
                                    TL.LogMessage("Main", $"  Ignoring type {type.Name} because it is not static.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            TL.LogMessage("Main", $"Exception (inner) when disposing of hardware class.\r\n{ex}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TL.LogMessage("Main", $"Exception (outer) when disposing of hardware class.\r\n{ex}");
                }

                // Now stop the Garbage Collector thread.
                TL.LogMessage("Main", $"Stopping garbage collector");
                StopGarbageCollection();
            }

            TL.LogMessage("Main", $"Local server closing");
            TL.Dispose();

        }

        #endregion

        #region Server Lock, Object Counting, and AutoQuit on COM start-up

        /// <summary>
        /// Returns the total number of objects alive currently. 
        /// </summary>
        public static int ObjectCount
        {
            get
            {
                lock (lockObject)
                {
                    return driversInUseCount; // Return the object count
                }
            }
        }

        /// <summary>
        /// Performs a thread-safe incrementation of the object count. 
        /// </summary>
        /// <returns></returns>
        public static int IncrementObjectCount()
        {
            int newCount = Interlocked.Increment(ref driversInUseCount); // Increment the object count.
            TL.LogMessage("IncrementObjectCount", $"New object count: {newCount}");

            return newCount;
        }

        /// <summary>
        /// Performs a thread-safe decrementation the objects count.
        /// </summary>
        /// <returns></returns>
        public static int DecrementObjectCount()
        {
            int newCount = Interlocked.Decrement(ref driversInUseCount); // Decrement the object count.
            TL.LogMessage("DecrementObjectCount", $"New object count: {newCount}");

            return newCount;
        }

        /// <summary>
        /// Returns the current server lock count.
        /// </summary>
        public static int ServerLockCount
        {
            get
            {
                lock (lockObject)
                {
                    return serverLockCount; // Return the lock count
                }
            }
        }

        /// <summary>
        /// Performs a thread-safe incrementation of the server lock count. 
        /// </summary>
        /// <returns></returns>
        public static int IncrementServerLockCount()
        {
            int newCount = Interlocked.Increment(ref serverLockCount); // Increment the server lock count for this server.
            TL.LogMessage("IncrementServerLockCount", $"New server lock count: {newCount}");

            return newCount;
        }

        /// <summary>
        /// Performs a thread-safe decrementation the server lock count.
        /// </summary>
        /// <returns></returns>
        public static int DecrementServerLockLock()
        {
            int newCount = Interlocked.Decrement(ref serverLockCount); // Decrement the server lock count for this server.
            TL.LogMessage("DecrementServerLockLock", $"New server lock count: {newCount}");
            return newCount;
        }

        /// <summary>
        /// Test whether the objects count and server lock count have both dropped to zero and, if so, terminate the application.
        /// </summary>
        /// <remarks>
        /// If the counts are zero, the application is terminated by posting a WM_QUIT message to the main thread's message loop, causing it to terminate and exit.
        /// </remarks>
        public static void ExitIf()
        {
            lock (lockObject)
            {
                TL.LogMessage("ExitIf", $"Object count: {ObjectCount}, Server lock count: {serverLockCount}");
                if ((ObjectCount <= 0) && (ServerLockCount <= 0))
                {
                    if (startedByCOM)
                    {
                        TL.LogMessage("ExitIf", $"Server started by COM so shutting down the Windows message loop on the main process to end the local server.");

                        UIntPtr wParam = new UIntPtr(0);
                        IntPtr lParam = new IntPtr(0);
                        PostThreadMessage(mainThreadId, 0x0012, wParam, lParam);
                    }
                }
            }
        }

        #endregion

        #region Dynamic Driver Assembly Loader

        /// <summary>
        /// Populates the list of ASCOM drivers by searching for driver classes within the local server executable.
        /// </summary>
        /// <returns>True if successful, otherwise False</returns>
        private static bool PopulateListOfAscomDrivers()
        {
            // Initialise the driver types list
            driverTypes = new ArrayList();

            try
            {
                // Get the types contained within the local server assembly
                Assembly so = Assembly.GetExecutingAssembly(); // Get the local server assembly 
                Type[] types = so.GetTypes(); // Get the types in the assembly

                //Add any Pre-Configured dynamic types
                types = types.Concat(GetDynamicTypes()).ToArray();

                // Iterate over the types identifying those which are drivers
                foreach (Type type in types)
                {
                    TL.LogMessage("PopulateListOfAscomDrivers", $"Found type: {type.Name}");

                    // Check to see if this type has the ServedClassName attribute, which indicates that this is a driver class.
                    object[] attrbutes = type.GetCustomAttributes(typeof(ServedClassNameAttribute), false);
                    if (attrbutes.Length > 0) // There is a ServedClassName attribute on this class so it is a driver
                    {
                        TL.LogMessage("PopulateListOfAscomDrivers", $"  {type.Name} is a driver assembly");
                        driverTypes.Add(type); // Add the driver type to the list
                    }
                }
                TL.BlankLine();

                // Log discovered drivers
                TL.LogMessage("PopulateListOfAscomDrivers", $"Found {driverTypes.Count} drivers");
                foreach (Type type in driverTypes)
                {
                    TL.LogMessage("PopulateListOfAscomDrivers", $"Found Driver : {type.Name}");
                }
                TL.BlankLine();
            }
            catch (Exception e)
            {
                TL.LogMessage("PopulateListOfAscomDrivers", $"Exception: {e}");
                MessageBox.Show($"Failed to load served COM class assembly from within this local server - {e.Message}", "Rotator Simulator", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return false;
            }

            return true;
        }

        //Returns a list of the dynamically generated driver types.
        internal static IEnumerable<Type> GetDynamicTypes()
        {
            //I'm just going to hard code 3 here. You can store a count and dynamically generate as many as you want.
            //To add and hot reload the drivers
            /*
             * 1. Add the new driver instance to the persistent storage
             * 2. Start a second instance of this LocalServer calling the Register command (this will elevate it)
             * 3. The second instance loads the persistent storage and finds and registers the new instances
             * 4. The second instance closes
             * 5. Call PopulateListOfAscomDrivers again in the original instance after registration is complete. It will find the added drivers and load them into memory
             * 6. Clients can now use those drivers and can still access any drivers they were using as the parent instance never closed.
             * 
             */
            List<Type> types = new List<Type>
            {
                //This was the one that the driver started with
                GenerateTypeWithAttributes("Focuser", new Guid("A8904146-656B-4852-96CB-53C1229FF0E8"), "ASCOM.AlpacaSimulator.Focuser", "ASCOM Focuser Driver for Alpaca Demo", typeof(ASCOM.Simulators.LocalServer.Drivers.Focuser), typeof(ASCOM.DeviceInterface.IFocuserV3)),
                GenerateTypeWithAttributes("SafetyMonitor", new Guid("269F2A82-98B6-46EE-88F7-5A6C794E5D9A"), "ASCOM.AlpacaSimulator.SafetyMonitor", "ASCOM SafetyMonitor Driver for Alpaca Demo", typeof(ASCOM.Simulators.LocalServer.Drivers.SafetyMonitor), typeof(ASCOM.DeviceInterface.ISafetyMonitor)),
            };

            return types;
        }


        internal static Type GenerateTypeWithAttributes(string DeviceType, Guid DriverGuid, string ProgID, string ServedName, Type driverType, Type interfaceType)
        {
            var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(ProgID), AssemblyBuilderAccess.Run);
            var mb = ab.DefineDynamicModule("DynamicCOMDrivers");

            var tb = mb.DefineType(DeviceType, TypeAttributes.Class | TypeAttributes.Public, driverType);
            tb.AddInterfaceImplementation(interfaceType);
            tb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProgIdAttribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(string) }, null), new object[] { ProgID }));

            tb.SetCustomAttribute(new CustomAttributeBuilder(typeof(GuidAttribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(string) }, null), new object[] { DriverGuid.ToString() }));

            tb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ServedClassNameAttribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(string) }, null), new object[] { ServedName }));

            tb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ClassInterfaceAttribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(ClassInterfaceType) }, null), new object[] { ClassInterfaceType.None }));

            return tb.CreateType();
        }

        #endregion

        #region COM Registration and Unregistration

        /// <summary>
        /// Register drivers contained in this local server. (Must run as Administrator.)
        /// </summary>
        /// <remarks>
        /// Do everything to register this for COM. Never use REGASM on this exe assembly! It would create InProcServer32 entries which would prevent proper activation!
        /// Using the list of COM object types generated during dynamic assembly loading, this method registers each driver for COM and registers it for ASCOM. 
        /// It also adds DCOM info for the local server itself, so it can be activated via an outbound connection from TheSky.
        /// </remarks>
        private static void RegisterObjects()
        {
            // Request administrator privilege if we don't already have it
            if (!IsAdministrator)
            {
                ElevateSelf("/register");
                return;
            }

            // If we reach here, we're running elevated

            // Initialise variables
            Assembly executingAssembly = Assembly.GetExecutingAssembly();
            Attribute assemblyTitleAttribute = Attribute.GetCustomAttribute(executingAssembly, typeof(AssemblyTitleAttribute));
            string assemblyTitle = ((AssemblyTitleAttribute)assemblyTitleAttribute).Title;
            assemblyTitleAttribute = Attribute.GetCustomAttribute(executingAssembly, typeof(AssemblyDescriptionAttribute));
            string assemblyDescription = ((AssemblyDescriptionAttribute)assemblyTitleAttribute)?.Description ?? "ASCOM Local Server for Simulators and Alpaca";

            // Set the local server's DCOM/AppID information
            try
            {
                TL.LogMessage("RegisterObjects", $"Setting local server's APPID");

                // Set HKCR\APPID\appid
                using (RegistryKey appIdKey = Registry.ClassesRoot.CreateSubKey($"APPID\\{localServerAppId}"))
                {
                    appIdKey.SetValue(null, assemblyDescription);
                    appIdKey.SetValue("AppID", localServerAppId);
                    appIdKey.SetValue("AuthenticationLevel", 1, RegistryValueKind.DWord);
                    appIdKey.SetValue("RunAs", "Interactive User", RegistryValueKind.String); // Added to ensure that only one copy of the local server runs if the user uses both elevated and non-elevated clients concurrently
                }

                // Set HKCR\APPID\exename.ext
                using (RegistryKey exeNameKey = Registry.ClassesRoot.CreateSubKey($"APPID\\{Application.ExecutablePath.Substring(Application.ExecutablePath.LastIndexOf('\\') + 1)}"))
                {
                    exeNameKey.SetValue("AppID", localServerAppId);
                }
                TL.LogMessage("RegisterObjects", $"APPID set successfully");
            }
            catch (Exception ex)
            {
                TL.LogMessage("RegisterObjects", $"Setting AppID exception: {ex}");
                MessageBox.Show("Error while registering the server:\n" + ex.ToString(), "ASCOM.DynamicDemo.SafetyMonitor", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return;
            }

            // Register each discovered driver
            foreach (Type driverType in driverTypes)
            {
                TL.LogMessage("RegisterObjects", $"Creating COM registration for {driverType.Name}");
                bool bFail = false;
                try
                {
                    // HKCR\CLSID\clsid
                    string clsId = Marshal.GenerateGuidForType(driverType).ToString("B");
                    string progId = Marshal.GenerateProgIdForType(driverType);
                    string deviceType = driverType.Name; // Generate device type from the Class name
                    TL.LogMessage("RegisterObjects", $"Assembly title: {assemblyTitle}, ASsembly description: {assemblyDescription}, CLSID: {clsId}, ProgID: {progId}, Device type: {deviceType}");

                    using (RegistryKey clsIdKey = Registry.ClassesRoot.CreateSubKey($"CLSID\\{clsId}"))
                    {
                        clsIdKey.SetValue(null, progId);
                        clsIdKey.SetValue("AppId", localServerAppId);
                        using (RegistryKey implementedCategoriesKey = clsIdKey.CreateSubKey("Implemented Categories"))
                        {
                            implementedCategoriesKey.CreateSubKey("{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}");
                        }

                        using (RegistryKey progIdKey = clsIdKey.CreateSubKey("ProgId"))
                        {
                            progIdKey.SetValue(null, progId);
                        }
                        clsIdKey.CreateSubKey("Programmable");

                        using (RegistryKey localServer32Key = clsIdKey.CreateSubKey("LocalServer32"))
                        {
                            localServer32Key.SetValue(null, Application.ExecutablePath);
                        }
                    }

                    // HKCR\CLSID\progid
                    using (RegistryKey progIdKey = Registry.ClassesRoot.CreateSubKey(progId))
                    {
                        progIdKey.SetValue(null, assemblyTitle);
                        using (RegistryKey clsIdKey = progIdKey.CreateSubKey("CLSID"))
                        {
                            clsIdKey.SetValue(null, clsId);
                        }
                    }

                    // Pull the display name from the ServedClassName attribute.
                    assemblyTitleAttribute = Attribute.GetCustomAttribute(driverType, typeof(ServedClassNameAttribute));
                    string chooserName = ((ServedClassNameAttribute)assemblyTitleAttribute).DisplayName ?? "MultiServer";
                    TL.LogMessage("RegisterObjects", $"Registering {chooserName} ({driverType.Name}) in Profile");

                    Profile.Register(Devices.StringToDeviceType(deviceType), progId, chooserName);
                }
                catch (Exception ex)
                {
                    TL.LogMessage("RegisterObjects", $"Driver registration exception: {ex}");
                    MessageBox.Show("Error while registering the server:\n" + ex.ToString(), "ASCOM.DynamicDemo.SafetyMonitor", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    bFail = true;
                }

                // Stop processing drivers if something has gone wrong
                if (bFail) break;
            }
        }

        /// <summary>
        /// Unregister drivers contained in this local server. (Must run as administrator.)
        /// </summary>
        private static void UnregisterObjects()
        {
            // Request administrator privilege if we don't already have it
            if (!IsAdministrator)
            {
                ElevateSelf("/unregister");
                return;
            }

            // If we reach here, we're running elevated

            // Delete the Local Server's DCOM/AppID information
            Registry.ClassesRoot.DeleteSubKey($"APPID\\{localServerAppId}", false);
            Registry.ClassesRoot.DeleteSubKey($"APPID\\{Application.ExecutablePath.Substring(Application.ExecutablePath.LastIndexOf('\\') + 1)}", false);

            // Delete each driver's COM registration
            foreach (Type driverType in driverTypes)
            {
                TL.LogMessage("UnregisterObjects", $"Removing COM registration for {driverType.Name}");

                string clsId = Marshal.GenerateGuidForType(driverType).ToString("B");
                string progId = Marshal.GenerateProgIdForType(driverType);

                // Remove ProgID entries
                Registry.ClassesRoot.DeleteSubKey($"{progId}\\CLSID", false);
                Registry.ClassesRoot.DeleteSubKey(progId, false);

                // Remove CLSID entries
                Registry.ClassesRoot.DeleteSubKey($"CLSID\\{clsId}\\Implemented Categories\\{{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}}", false);
                Registry.ClassesRoot.DeleteSubKey($"CLSID\\{clsId}\\Implemented Categories", false);
                Registry.ClassesRoot.DeleteSubKey($"CLSID\\{clsId}\\ProgId", false);
                Registry.ClassesRoot.DeleteSubKey($"CLSID\\{clsId}\\LocalServer32", false);
                Registry.ClassesRoot.DeleteSubKey($"CLSID\\{clsId}\\Programmable", false);
                Registry.ClassesRoot.DeleteSubKey($"CLSID\\{clsId}", false);

                // Uncomment the following lines to remove ASCOM Profile information when unregistering.
                // Unregistering often occurs during version upgrades and, if the code below is enabled, will result in loss of all device configuration during the upgrade.
                // For this reason, enabling this capability is not recommended.

                //try
                //{
                //    TL.LogMessage("UnregisterObjects", $"Deleting ASCOM Profile registration for {driverType.Name} ({progId})");
                //    using (var profile = new Profile())
                //    {
                //        profile.DeviceType = driverType.Name;
                //        profile.Unregister(progId);
                //    }
                //}
                //catch (Exception) { }
            }
        }

        /// <summary>
        /// Test whether the session is running with elevated credentials
        /// </summary>
        private static bool IsAdministrator
        {
            get
            {
                WindowsIdentity userIdentity = WindowsIdentity.GetCurrent();
                WindowsPrincipal userPrincipal = new WindowsPrincipal(userIdentity);
                bool isAdministrator = userPrincipal.IsInRole(WindowsBuiltInRole.Administrator);

                TL.LogMessage("IsAdministrator", isAdministrator.ToString());
                return isAdministrator;
            }
        }

        /// <summary>
        /// Elevate privileges by re-running ourselves with elevation dialogue
        /// </summary>
        /// <param name="argument">Argument to pass to ourselves</param>
        private static void ElevateSelf(string argument)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            processStartInfo.Arguments = argument;
            processStartInfo.WorkingDirectory = Environment.CurrentDirectory;
            processStartInfo.FileName = Application.ExecutablePath;
            processStartInfo.UseShellExecute = true;
            processStartInfo.Verb = "runas";
            try
            {
                TL.LogMessage("IsAdministrator", $"Starting elevated process");
                Process.Start(processStartInfo);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                TL.LogMessage("IsAdministrator", $"The ASCOM.DynamicDemo.SafetyMonitor was not " + (argument == "/register" ? "registered" : "unregistered because you did not allow it."));
                MessageBox.Show("The ASCOM.DynamicDemo.SafetyMonitor was not " + (argument == "/register" ? "registered" : "unregistered because you did not allow it.", "ASCOM.DynamicDemo.SafetyMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
            catch (Exception ex)
            {
                TL.LogMessage("IsAdministrator", $"Exception: {ex}");
                MessageBox.Show(ex.ToString(), "ASCOM.DynamicDemo.SafetyMonitor", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            }
            return;
        }

        #endregion

        #region Class Factory Support

        /// <summary>
        /// Register the class factories of drivers that this local server serves.
        /// </summary>
        /// <remarks>This requires the class factory name to be equal to the served class name + "ClassFactory".</remarks>
        /// <returns>True if there are no errors, otherwise false.</returns>
        private static bool RegisterClassFactories()
        {
            TL.LogMessage("RegisterClassFactories", $"Registering class factories");
            classFactories = new ArrayList();
            foreach (Type driverType in driverTypes)
            {
                TL.LogMessage("RegisterClassFactories", $"  Creating class factory for: {driverType.Name}");
                ClassFactory factory = new ClassFactory(driverType); // Use default context & flags
                classFactories.Add(factory);

                TL.LogMessage("RegisterClassFactories", $"  Registering class factory for: {driverType.Name}");
                if (!factory.RegisterClassObject())
                {
                    TL.LogMessage("RegisterClassFactories", $"  Failed to register class factory for " + driverType.Name);
                    MessageBox.Show("Failed to register class factory for " + driverType.Name, "ASCOM.DynamicDemo.SafetyMonitor", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    return false;
                }
                TL.LogMessage("RegisterClassFactories", $"  Registered class factory OK for: {driverType.Name}");
            }

            TL.LogMessage("RegisterClassFactories", $"Making class factories live");
            ClassFactory.ResumeClassObjects(); // Served objects now go live
            TL.LogMessage("RegisterClassFactories", $"Class factories live OK");
            return true;
        }

        /// <summary>
        /// Revoke the class factories
        /// </summary>
        private static void RevokeClassFactories()
        {
            TL.LogMessage("RevokeClassFactories", $"Suspending class factories");
            ClassFactory.SuspendClassObjects(); // Prevent race conditions
            TL.LogMessage("RevokeClassFactories", $"Class factories suspended OK");

            foreach (ClassFactory factory in classFactories)
            {
                factory.RevokeClassObject();
            }
        }

        #endregion

        #region Command line argument processing

        /// <summary>
        ///Process the command-line arguments returning true to continue execution or false to terminate the application immediately.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private static bool ProcessArguments(string[] args)
        {
            bool returnStatus = true;

            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "-embedding":
                        TL.LogMessage("ProcessArguments", $"Started by COM: {args[0]}");
                        startedByCOM = true; // Indicate COM started us and continue
                        returnStatus = true; // Continue on return
                        break;

                    case "-register":
                    case @"/register":
                    case "-regserver": // Emulate VB6
                    case @"/regserver":
                        TL.LogMessage("ProcessArguments", $"Registering drivers: {args[0]}");
                        RegisterObjects(); // Register each served object
                        returnStatus = false; // Terminate on return
                        break;

                    case "-unregister":
                    case @"/unregister":
                    case "-unregserver": // Emulate VB6
                    case @"/unregserver":
                        TL.LogMessage("ProcessArguments", $"Unregistering drivers: {args[0]}");
                        UnregisterObjects(); //Unregister each served object
                        returnStatus = false; // Terminate on return
                        break;

                    default:
                        TL.LogMessage("ProcessArguments", $"Unknown argument: {args[0]}");
                        MessageBox.Show("Unknown argument: " + args[0] + "\nValid are : -register, -unregister and -embedding", "ASCOM.DynamicDemo.SafetyMonitor", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        break;
                }
            }
            else
            {
                startedByCOM = false;
                TL.LogMessage("ProcessArguments", $"No arguments supplied");
            }

            return returnStatus;
        }

        #endregion

        #region Garbage collection support

        /// <summary>
        /// Start a garbage collection thread that can be cancelled
        /// </summary>
        /// <param name="interval">Frequency of garbage collections</param>
        private static void StartGarbageCollection(int interval)
        {
            // Create the garbage collection object
            TL.LogMessage("StartGarbageCollection", $"Creating garbage collector with interval: {interval} seconds");
            GarbageCollection garbageCollector = new GarbageCollection(interval);

            // Create a cancellation token and start the garbage collection task 
            TL.LogMessage("StartGarbageCollection", $"Starting garbage collector thread");
            GCTokenSource = new CancellationTokenSource();
            GCTask = Task.Factory.StartNew(() => garbageCollector.GCWatch(GCTokenSource.Token), GCTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            TL.LogMessage("StartGarbageCollection", $"Garbage collector thread started OK");
        }


        /// <summary>
        /// Stop the garbage collection task by sending it the cancellation token and wait for the task to complete
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "The program is ending at this point so the synchronous wait is justified to ensure that it completes.")]
        private static void StopGarbageCollection()
        {
            // Signal the garbage collector thread to stop
            TL.LogMessage("StopGarbageCollection", $"Stopping garbage collector thread");
            GCTokenSource.Cancel();
            GCTask.Wait();
            TL.LogMessage("StopGarbageCollection", $"Garbage collector thread stopped OK");

            // Clean up
            GCTask = null;
            GCTokenSource.Dispose();
            GCTokenSource = null;
        }

        #endregion

        #region kernel32.dll and user32.dll functions

        // Post a Windows Message to a specific thread (identified by its thread id). Used to post a WM_QUIT message to the main thread in order to terminate this application.)
        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);

        // Obtain the thread id of the calling thread allowing us to post the WM_QUIT message to the main thread.
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        #endregion
    }
}

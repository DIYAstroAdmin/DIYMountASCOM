# ASCOM LocalServer Driver

#### You have just created an ASCOM Local Server driver.

This project creates an ASCOM local server executable and a driver class that will support multiple clients. It can also be extended to serve multiple ASCOM devices such as a Telescope and a Focuser or multiple devices of the same type such as two focusers.

> [!IMPORTANT]
> **BEFORE CONTINUING - Please consider creating a cross-platform Alpaca device instead of a COM local server driver.**

To ensure longevity of your device, ASCOM now recommends building Alpaca devices rather than COM drivers. The benefits you reap by switching to Alpaca are fully explained here: [Introduction to ASCOM Alpaca](https://www.ascom-alpaca.org/).

An Alpaca device's RESTful web API can be used by any desktop or mobile application that can make HTTP requests, running on any operating system. This makes it much more flexible than a local server, whose use is limited to COM clients that run on Windows.

You lose nothing by developing an Alpaca device! A growing number of client applications have native Alpaca support while the Platform's built-in Dynamic Clients enable traditional COM clients to use Alpaca devices. In addition the ASCOM Library includes many useful features that make it easy to develop Alpaca devices: [ASCOM Library](https://ascom-standards.org/library). The Library is available as a package on NuGet that you can simply add to your project.

You can find more information about developing for Alpaca here: [Developing for Alpaca](https://ascom-standards.org/AlpacaDeveloper/Index.htm).

> [!NOTE]
> Modern astronomical observatories often use multiple clients concurrently such as planetariums, session managers and device managers and typically these clients expect drivers to handle concurrent access to ASCOM interface properties and methods. To be successful, your driver will therefore need to operate asynchronously e.g. by being able to respond to device position properties while the device is moving.
>
> Device implementation complexity is closely related to ASCOM device interface complexity. In broadly increasing order of complexity the ASCOM interfaces are: SafetyMonitor, ObservingConditions, FilterWheel, CoverCalibrator, Focuser, Rotator, Switch, Camera, Dome, Video and Telescope.
>
> In addition to responding to asynchronous client requests, you will need to reliably serialise communications with your hardware if it does not intrinsically support concurrent operation. This means that you will need to be familiar with asynchronous programming techniques or be prepared to navigate a steep learning curve.

You're probably anxious to get going, but if you are new to this it will help to read through the short [Theory of Operation](#theory-of-operation) and [Disposing of Resources](#disposing-of-resources) sections below.

## Driver Architecture

The Telescope driver created by the local server project template comprises these main layers:

1. **`TelescopeDriver\TelescopeDriver.cs`** - An ASCOM COM driver that is served to clients by the local server. An instance of this class will be created every time a client creates an instance of your Telescope driver. This class is mainly a presentation layer for the shared functionality that is contained in the `Telescope\TelescopeHardware` class and should require little customisation.

2. **`TelescopeDriver\TelescopeHardware.cs`** - An internal static class that is shared by all instances of your `TelescopeDriver` class. This is where you should locate capabilities that will be shared by all instances of your Telescope driver. This static class will contain most of the heavy lifting to map the ASCOM interface functions to the specific communications protocol and behavioural requirements of your Telescope hardware. The main functionality is implemented in this shared class so that you have the opportunity to manage concurrent requests from multiple instances of your Telescope driver created by one or more ASCOM client applications. Ordinarily, the capabilities in this file would not be shared with other drivers that you may add to the local server at a later date.

3. **`SharedResources.cs`** - This is where you should place resources that will be shared between the different drivers served by the local server. Typically communications functionality that is shared across multiple drivers will go here. In the generated project, some basic serial port functionality is located here.

4. **Local server operation support files** - The remaining files in the generated project support local server operation and do not need to be customised further. Please do not change these files unless you are completely sure you know what you are doing!

## To complete your local server driver, please follow these steps in order:

1. **Review the local server's project properties:**
   - Review the Application tab and change the AssemblyName and default namespace if required, the recommended format is `ASCOM.xxx`.
   - Click the Assembly Information button and review and update the text if required.
   - Do not check the "Make assembly COM visible" checkbox; the local server will register the COM drivers dynamically at run-time, and checking this box interferes with the dynamic registration process.
   - Change the assembly version number if required but do not use wild-card (`*`) numbering because it causes issues with COM registration. It is not recommended that the assembly version number is changed on each release, use the file version number to identify different releases.
   - Change the assembly file version if required, this can use wild-card numbering.
   - Do not change the Platform Target on the Build tab from x86, this must remain as set for correct local server operation. The local server runs as a fully independent process rather than as a thread within the client application and behind the scenes COM automatically translates between both 32bit and 64bit client processes and the 32bit local server process.

2. **Check and test the driver created from the template:**
   - Review the ProgID and ServedClassName fields above the Class name for suitability.
   - Compile the whole solution but don't run it.
   - Open a command prompt at the folder where the local server executable is located.
   - Run the local server exe with the `/regserver` parameter (requires admin privilege), which will create the entry that appears in the ASCOM Chooser. This registration only needs to be done once on each PC. Use the `/unregserver` parameter to unregister the server when no longer required. Never use REGASM on the local server executable, this way lies confusion and much time wasting!
   - Close the command prompt.
   - Start an ASCOM client application or Conform or a Connection form in the Diagnostics tool and use the client to open a Chooser for the device type of your driver. You will see your driver in the list, will be able to select it and open its Setup dialogue.
   - To support debugging, the driver will create three log files in a `Documents\ASCOM\Logs yyyy-mm-dd` folder:
     1. **`ASCOM.DIYAstroMount.Driver.xxx`** - One log per instance of the COM driver class. (Can be enabled / disabled through the Driver setup dialogue.)
     2. **`ASCOM.DIYAstroMount.Hardware.xxx`** - One log from the shared hardware class containing information from all instances of the COM driver. (Can be enabled / disabled through the Driver setup dialogue.)
     3. **`ASCOM.DIYAstroMount.LocalServer.xxx`** - One log for the local server executable itself. (After the driver is running stably, this log can be disabled if required by editing the `TL.Enabled` statement near the top of the `LocalServer.cs` file.)
   - At this point the basics are proved and its time to start making the driver do something! If you have issues in getting to this point, check the logs in your `Documents\ASCOM` folder, which will help determine what has gone wrong. You are welcome to post at the ASCOM Developer forum listed below if you get stuck.

3. **Implement the driver methods:**
   - The first implementation task is to fully implement the `Connected` property to establish communications to your device hardware.
   - After this, it's a good idea to implement a straightforward property before moving on to more complex properties and finally to methods. This approach will enable you to focus on the essential driver / application infrastructure initially before having to deal with more complex device functions.

## How to add further drivers if required

This method works when:

- Adding a completely new served device type e.g. adding a FOCUSER device to a local server already hosting a TELESCOPE device.
- Adding a second or further instance of an existing served device type e.g. adding a second FOCUSER to a local server already hosting a FOCUSER device.

#### Method

1. Add a new "Local Server" project to the solution ensuring that:
   - It is configured as the device type that you require.
   - Its ASCOM DeviceId is different to all other drivers in the local server.
   - You rename the driver folder in the new project if it matches one already present in original project.
2. Within Visual Studio drag and drop the driver folder from the new project to the root of the original project.
3. Delete the new local server project added in step 1.
4. Compile the project.
5. Register the driver as described above with `/regserver`.
6. Verify that the driver is visible in an application Chooser dialogue.
7. Implement the new driver's methods.

## Notes

- If required, a serial port is already provided (see `SharedResources.cs`) as `SharedResources.SharedSerial`. If you don't need the serial port you can remove the public static `SharedSerial` property, the `m_SharedSerial` member in the private data region, and the line in main that initializes it.
- If you want to control or report status information about devices hosted by the local server the best approach is to create a separate application that connects to the served driver(s).
- If you only want to display status information, a simpler option is to add a form to the local server that uses a timed loop to poll driver(s) and update the status information.

## Theory of Operation

The local server is an executable that can provide multiple instances of multiple drivers to multiple clients, and which supports two core capabilities:

- Being a hub, which allows multiple clients to share a single device.
- Supporting hardware that contains multiple devices such as a telescope that has a built-in focuser, where both the telescope and focuser are controlled by the same connection.

A driver is an assembly which contains a class that *implements* one of the ASCOM standard driver interfaces and *inherits* the `ReferenceCountedObjectBase` class of the local server.

The fact that driver classes inherit from the local server's `ReferenceCountedObjectBase` class allows the local server to maintain a reference count on the driver class. If a client creates an instance of a served driver, the local server automatically starts up and provides an instance of the class to the client. Once started the local server can provide additional instances of any of its served driver classes. If the reference count of all served classes drops to zero as a result of clients releasing their instances, the local server will automatically exit.

The local server's registration services include not only basic COM class registration, but also the DCOM/AppID info needed to use the served classes from outbound connections from Software Bisque's TheSky. It also registers the served classes for the ASCOM Chooser. The "friendly" name of each served driver that appears in the chooser comes from the driver's `ServedClassName` attribute. The COM ProgID for each served driver is specified in the `ProgId` attribute for example, `ASCOM.AlphaTech.Telescope`, where AlphaTech is the local server name and Telescope is the ASCOM type of the driver.

## Disposing of Resources

If you create objects that consume significant memory or that use finite operating system resources such as file handles, synchronisation objects etc. you will want to release these back to the operating system as soon as you know that clients have finished with them. This is where the `Dispose` methods on your driver, hardware class and the shared resources class come in.

The `TelescopeDriver` class is instantiable and supports the `IDisposable` pattern. This means that clients can inform your class that it is no longer needed, so that it can release its resources. It doesn't matter if clients don't call the `Dispose()` method because the `IDisposable` interface implemented in the driver ensures that the runtime's garbage collector will call the driver instance's `Dispose()` method and your resources will then be released.

Both the static `TelescopeHardware` and `SharedResources` classes are decorated with the `[HardwareClass()]` attribute, which the local server will detect when it is about to close down. The local server will then automatically call the `Dispose()` methods on these classes without requiring any action on your part.

If you create further static classes that need to be disposed when the local server shuts down, just add a hardware class attribute line (`[HardwareClass()]`) immediately above your class definition line (`internal static class YOURCLASSNAME`) and add a public `Dispose()` method to your class.

Please do not call the static class `Dispose()` methods from the `TelescopeDriver.Dispose()` method. The static class lifetimes are linked to the lifetime of the local server application and should be Disposed only when it is clear that the local server is irretrievably closing down, as already implemented in the automatic disposal mechanism built into the local server.

## ASCOM Initiative

The ASCOM Initiative consists of a group of astronomy software developers and instrument vendors whose goals are to promote the driver/client model and scripting automation.

See the [ASCOM web site](https://ascom-standards.org/) for more information. If you have questions, please post here: [ASCOM-Talk Developer Forum](https://ascomtalk.groups.io/g/Developer/topics).

//
// ASCOM Telescope hardware class for DIYAstroMount
//
// Description:	 Drives the DIY German equatorial mount (FYSETC E4 / ESP32 firmware) over its
//               USB serial port using the LX200 command set implemented in Firmware/src/LX200.cpp.
//
// Implements:	ASCOM Telescope interface version: 4
//
// Notes on the firmware's LX200 dialect that shape this driver:
//  - Positions are always reported in high precision (HH:MM:SS / sDD*MM'SS).
//  - :D# returns 0x7F while a goto, park or home search runs; manual moves are not reported,
//    so the driver tracks them itself.
//  - :GW# returns "G" + (T|N tracking) + (P parked | 1).
//  - There is no command to read the tracking rate, guide rate or slew rate, nor to read
//    pulse-guide state. The tracking rate is cached by the driver, the guide and slew rates
//    are configured in the setup dialogue, and pulse-guide state is timed by the driver.
//  - The four manual move rates are G (guide rate), C (16x sidereal), M (slew rate / 4) and
//    S (slew rate), so MoveAxis accepts exactly those four rates.

using ASCOM.Astrometry.AstroUtils;
using ASCOM.DeviceInterface;
using ASCOM.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace ASCOM.DIYAstroMount.Telescope
{
    /// <summary>
    /// ASCOM Telescope hardware class for DIYAstroMount.
    /// </summary>
    [HardwareClass()] // Class attribute flag this as a device hardware class that needs to be disposed by the local server when it exits.
    internal static class TelescopeHardware
    {
        // Constants used for Profile persistence
        internal const string comPortProfileName = "COM Port";
        internal const string comPortDefault = "COM1";
        internal const string traceStateProfileName = "Trace Level";
        internal const string traceStateDefault = "true";
        internal const string guideRateProfileName = "Guide Rate";       // multiples of sidereal
        internal const string guideRateDefault = "0.5";                  // firmware DEFAULT_GUIDE_RATE
        internal const string slewRateProfileName = "Slew Rate";         // deg/s
        internal const string slewRateDefault = "2.0";                   // firmware DEFAULT_SLEW_RATE
        internal const string syncTimeProfileName = "Sync Time On Connect";
        internal const string syncTimeDefault = "true";

        internal const int BaudRate = 9600;                              // firmware LX200_BAUD
        internal const double SiderealDegPerSec = 360.0 / 86164.0905;
        internal const double CenterRateSidereal = 16.0;                 // firmware CENTER_RATE_SIDEREAL
        internal const int MaxPulseMs = 30000;                           // firmware clamps :Mg durations to this
        private const int SlewTimeoutMs = 10 * 60 * 1000;                // park / home / synchronous goto
        private const int PollIntervalMs = 250;

        private static string DriverProgId = ""; // ASCOM DeviceID (COM ProgID) for this driver, the value is set by the driver's class initialiser.
        private static string DriverDescription = ""; // The value is set by the driver's class initialiser.
        internal static string comPort; // COM port name
        internal static double guideRateSidereal = 0.5;
        internal static double slewRateDegPerSec = 2.0;
        internal static bool syncTimeOnConnect = true;

        private static bool connectedState; // Local server's connected state
        private static bool runOnce = false; // Flag to enable "one-off" activities only to run once.
        internal static Util utilities; // ASCOM Utilities object for use as required
        internal static AstroUtils astroUtilities; // ASCOM AstroUtilities object for use as required
        internal static TraceLogger tl; // Local server's trace logger object for diagnostic log with information that you specify

        private static readonly List<Guid> uniqueIds = new List<Guid>(); // List of driver instance unique IDs
        private static readonly object connectLock = new object();
        private static Lx200Port mount;

        // Driver-side state for things the firmware cannot report over LX200
        private static readonly object stateLock = new object();
        private static readonly bool[] axisMoving = new bool[2];
        private static readonly DateTime[] pulseEnd = { DateTime.MinValue, DateTime.MinValue };
        private static DriveRates trackingRate = DriveRates.driveSidereal;
        private static double? targetRa;
        private static double? targetDec;
        private static double? lastSetLatitude;
        private static double? lastSetLongitude;
        private static short slewSettleTime = 0;
        private static string firmwareName = "";
        private static string firmwareVersion = "";

        /// <summary>
        /// Initializes a new instance of the device Hardware class.
        /// </summary>
        static TelescopeHardware()
        {
            try
            {
                // Create the hardware trace logger in the static initialiser.
                // All other initialisation should go in the InitialiseHardware method.
                tl = new TraceLogger("", "DIYAstroMount.Hardware");

                // DriverProgId has to be set here because it used by ReadProfile to get the TraceState flag.
                DriverProgId = Telescope.DriverProgId; // Get this device's ProgID so that it can be used to read the Profile configuration values

                // ReadProfile has to go here before anything is written to the log because it loads the TraceLogger enable / disable state.
                ReadProfile(); // Read device configuration from the ASCOM Profile store, including the trace state

                LogMessage("TelescopeHardware", $"Static initialiser completed.");
            }
            catch (Exception ex)
            {
                try { LogMessage("TelescopeHardware", $"Initialisation exception: {ex}"); } catch { }
                MessageBox.Show($"TelescopeHardware - {ex.Message}\r\n{ex}", $"Exception creating {Telescope.DriverProgId}", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
        }

        /// <summary>
        /// Place device initialisation code here
        /// </summary>
        /// <remarks>Called every time a new instance of the driver is created.</remarks>
        internal static void InitialiseHardware()
        {
            LogMessage("InitialiseHardware", $"Start.");

            if (runOnce == false)
            {
                LogMessage("InitialiseHardware", $"Starting one-off initialisation.");

                DriverDescription = Telescope.DriverDescription; // Get this device's Chooser description

                LogMessage("InitialiseHardware", $"ProgID: {DriverProgId}, Description: {DriverDescription}");

                connectedState = false; // Initialise connected to false
                utilities = new Util(); //Initialise ASCOM Utilities object
                astroUtilities = new AstroUtils(); // Initialise ASCOM Astronomy Utilities object
                mount = new Lx200Port(LogMessage);

                LogMessage("InitialiseHardware", $"One-off initialisation complete.");
                runOnce = true; // Set the flag to ensure that this code is not run again
            }
        }

        // PUBLIC COM INTERFACE ITelescopeV4 IMPLEMENTATION

        #region Common properties and methods.

        /// <summary>
        /// Displays the Setup Dialogue form.
        /// If the user clicks the OK button to dismiss the form, then
        /// the new settings are saved, otherwise the old values are reloaded.
        /// THIS IS THE ONLY PLACE WHERE SHOWING USER INTERFACE IS ALLOWED!
        /// </summary>
        public static void SetupDialog()
        {
            // Don't permit the setup dialogue if already connected
            if (IsConnected)
            {
                MessageBox.Show("Already connected, just press OK");
                return; // Exit the method if already connected
            }

            using (SetupDialogForm F = new SetupDialogForm(tl))
            {
                var result = F.ShowDialog();
                if (result == DialogResult.OK)
                {
                    WriteProfile(); // Persist device configuration values to the ASCOM Profile store
                }
                else
                {
                    ReadProfile(); // Discard any partial changes
                }
            }
        }

        /// <summary>Returns the list of custom action names supported by this driver.</summary>
        public static ArrayList SupportedActions
        {
            get
            {
                LogMessage("SupportedActions Get", "Returning empty ArrayList");
                return new ArrayList();
            }
        }

        /// <summary>Invokes the specified device-specific custom action.</summary>
        public static string Action(string actionName, string actionParameters)
        {
            LogMessage("Action", $"Action {actionName}, parameters {actionParameters} is not implemented");
            throw new ActionNotImplementedException("Action " + actionName + " is not implemented by this driver");
        }

        /// <summary>
        /// Transmits an arbitrary string to the device and does not wait for a response.
        /// With raw = false the LX200 framing ':' and '#' is added if missing.
        /// </summary>
        public static void CommandBlind(string command, bool raw)
        {
            CheckConnected("CommandBlind");
            if (raw) mount.WriteRaw(command);
            else mount.SendBlind(Unframe(command));
        }

        /// <summary>
        /// Transmits an arbitrary string to the device and waits for a single character reply,
        /// returning true if it is '1'.
        /// </summary>
        public static bool CommandBool(string command, bool raw)
        {
            CheckConnected("CommandBool");
            char reply;
            if (raw)
            {
                mount.WriteRaw(command);
                reply = mount.ReadRawChar();
            }
            else
            {
                reply = mount.SendChar(Unframe(command));
            }
            return reply == '1';
        }

        /// <summary>
        /// Transmits an arbitrary string to the device and waits for a '#'-terminated reply.
        /// The terminating '#' is not included in the returned string.
        /// </summary>
        public static string CommandString(string command, bool raw)
        {
            CheckConnected("CommandString");
            if (raw)
            {
                mount.WriteRaw(command);
                return mount.ReadRawToHash();
            }
            return mount.SendString(Unframe(command));
        }

        /// <summary>
        /// Deterministically release both managed and unmanaged resources that are used by this class.
        /// </summary>
        public static void Dispose()
        {
            try { LogMessage("Dispose", $"Disposing of assets and closing down."); } catch { }

            try { mount?.Dispose(); mount = null; } catch { }

            try
            {
                tl.Enabled = false;
                tl.Dispose();
                tl = null;
            }
            catch { }

            try
            {
                utilities.Dispose();
                utilities = null;
            }
            catch { }

            try
            {
                astroUtilities.Dispose();
                astroUtilities = null;
            }
            catch { }
        }

        /// <summary>
        /// Synchronously connects to or disconnects from the hardware
        /// </summary>
        /// <param name="uniqueId">Driver's unique ID</param>
        /// <param name="newState">New state: Connected or Disconnected</param>
        public static void SetConnected(Guid uniqueId, bool newState)
        {
            lock (connectLock)
            {
                if (newState)
                {
                    if (uniqueIds.Contains(uniqueId))
                    {
                        LogMessage("SetConnected", $"Ignoring request to connect because the device is already connected.");
                        return;
                    }

                    if (uniqueIds.Count == 0)
                    {
                        LogMessage("SetConnected", $"Connecting to hardware on {comPort}.");
                        ConnectHardware();
                    }
                    else
                    {
                        LogMessage("SetConnected", $"Hardware already connected.");
                    }

                    uniqueIds.Add(uniqueId);
                    LogMessage("SetConnected", $"Unique id {uniqueId} added to the connection list.");
                }
                else
                {
                    if (!uniqueIds.Contains(uniqueId))
                    {
                        LogMessage("SetConnected", $"Ignoring request to disconnect because the device is already disconnected.");
                        return;
                    }

                    uniqueIds.Remove(uniqueId);
                    LogMessage("SetConnected", $"Unique id {uniqueId} removed from the connection list.");

                    if (uniqueIds.Count == 0)
                    {
                        LogMessage("SetConnected", $"Disconnecting from hardware.");
                        DisconnectHardware();
                    }
                    else
                    {
                        LogMessage("SetConnected", $"Hardware still in use by other clients.");
                    }
                }

                LogMessage("SetConnected", $"Currently connected driver ids:");
                foreach (Guid id in uniqueIds)
                {
                    LogMessage("SetConnected", $" ID {id} is connected");
                }
            }
        }

        private static void ConnectHardware()
        {
            ReadProfile();
            try
            {
                mount.Open(comPort, BaudRate);
                firmwareName = mount.SendString("GVP");
                firmwareVersion = mount.SendString("GVN");
                LogMessage("ConnectHardware", $"Connected to {firmwareName} {firmwareVersion}");

                lock (stateLock)
                {
                    axisMoving[0] = axisMoving[1] = false;
                    pulseEnd[0] = pulseEnd[1] = DateTime.MinValue;
                    targetRa = targetDec = null;
                    lastSetLatitude = lastSetLongitude = null;
                }

                // The firmware has no command to read the tracking rate, so set a known one.
                mount.SendBlind("TQ");
                trackingRate = DriveRates.driveSidereal;

                connectedState = true;

                if (syncTimeOnConnect)
                {
                    SetMountUtc(DateTime.UtcNow);
                }
            }
            catch
            {
                connectedState = false;
                mount.Close();
                throw;
            }
        }

        private static void DisconnectHardware()
        {
            connectedState = false;
            mount.Close();
        }

        /// <summary>
        /// Returns a description of the device, such as manufacturer and model number. Any ASCII characters may be used.
        /// </summary>
        public static string Description
        {
            get
            {
                LogMessage("Description Get", DriverDescription);
                return DriverDescription;
            }
        }

        /// <summary>
        /// Descriptive and version information about this ASCOM driver.
        /// </summary>
        public static string DriverInfo
        {
            get
            {
                Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string driverInfo = $"LX200 serial driver for the DIY German equatorial mount. Version: {version.Major}.{version.Minor}";
                if (IsConnected && firmwareName.Length > 0)
                    driverInfo += $". Firmware: {firmwareName} {firmwareVersion}";
                LogMessage("DriverInfo Get", driverInfo);
                return driverInfo;
            }
        }

        /// <summary>
        /// A string containing only the major and minor version of the driver formatted as 'm.n'.
        /// </summary>
        public static string DriverVersion
        {
            get
            {
                Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string driverVersion = $"{version.Major}.{version.Minor}";
                LogMessage("DriverVersion Get", driverVersion);
                return driverVersion;
            }
        }

        /// <summary>
        /// The interface version number that this device supports.
        /// </summary>
        public static short InterfaceVersion
        {
            get
            {
                LogMessage("InterfaceVersion Get", "4");
                return 4;
            }
        }

        /// <summary>
        /// The short name of the driver, for display purposes
        /// </summary>
        public static string Name
        {
            get
            {
                string name = "DIY Mount (LX200)";
                LogMessage("Name Get", name);
                return name;
            }
        }

        #endregion

        #region ITelescope Implementation

        /// <summary>
        /// Stops a slew in progress.
        /// </summary>
        internal static void AbortSlew()
        {
            if (MountAtPark()) throw new ParkedException("AbortSlew: the mount is parked");
            mount.SendBlind("Q");
            lock (stateLock)
            {
                axisMoving[0] = axisMoving[1] = false;
            }
            LogMessage("AbortSlew", "Done");
        }

        /// <summary>
        /// The alignment mode of the mount (Alt/Az, Polar, German Polar).
        /// </summary>
        internal static AlignmentModes AlignmentMode
        {
            get
            {
                LogMessage("AlignmentMode Get", "algGermanPolar");
                return AlignmentModes.algGermanPolar;
            }
        }

        /// <summary>
        /// The Altitude above the local horizon of the telescope's current position (degrees, positive up)
        /// </summary>
        internal static double Altitude
        {
            get
            {
                double alt = ParseSexagesimal(mount.SendString("GA"), "GA");
                LogMessage("Altitude Get", alt.ToString(CultureInfo.InvariantCulture));
                return alt;
            }
        }

        internal static double ApertureArea
        {
            get
            {
                LogMessage("ApertureArea Get", "Not implemented");
                throw new PropertyNotImplementedException("ApertureArea", false);
            }
        }

        internal static double ApertureDiameter
        {
            get
            {
                LogMessage("ApertureDiameter Get", "Not implemented");
                throw new PropertyNotImplementedException("ApertureDiameter", false);
            }
        }

        /// <summary>
        /// True if the telescope is stopped in the home position (counterweight down, pointing at the pole).
        /// Uses the same test as the firmware's own Alpaca interface: idle, not tracking and both axes at zero.
        /// Axis 1 = 0 with axis 2 = 0 corresponds to hour angle +6h and |Dec| = 90.
        /// </summary>
        internal static bool AtHome
        {
            get
            {
                bool atHome = false;
                string status = mount.SendString("GW");
                bool tracking = status.Length > 1 && status[1] == 'T';
                if (!tracking && !MountBusy() && !AnyAxisMoving())
                {
                    double dec = ParseSexagesimal(mount.SendString("GD"), "GD");
                    if (Math.Abs(Math.Abs(dec) - 90.0) < 0.02)
                    {
                        double lst = ParseSexagesimal(mount.SendString("GS"), "GS");
                        double ra = ParseSexagesimal(mount.SendString("GR"), "GR");
                        double ha = NormaliseHours(lst - ra);
                        atHome = Math.Abs(ha - 6.0) < 0.005; // ~4.5 arcmin of hour angle
                    }
                }
                LogMessage("AtHome Get", atHome.ToString());
                return atHome;
            }
        }

        /// <summary>
        /// True if the telescope has been put into the parked state by the <see cref="Park" /> method.
        /// </summary>
        internal static bool AtPark
        {
            get
            {
                bool atPark = MountAtPark();
                LogMessage("AtPark Get", atPark.ToString());
                return atPark;
            }
        }

        /// <summary>
        /// Determine the rates at which the telescope may be moved about the specified axis by the <see cref="MoveAxis" /> method.
        /// </summary>
        internal static IAxisRates AxisRates(TelescopeAxes Axis)
        {
            LogMessage("AxisRates", "Get - " + Axis.ToString());
            return new AxisRates(Axis);
        }

        /// <summary>
        /// The azimuth at the local horizon of the telescope's current position (degrees, North-referenced, positive East/clockwise).
        /// </summary>
        internal static double Azimuth
        {
            get
            {
                double az = ParseSexagesimal(mount.SendString("GZ"), "GZ");
                LogMessage("Azimuth Get", az.ToString(CultureInfo.InvariantCulture));
                return az;
            }
        }

        internal static bool CanFindHome => LogCapability("CanFindHome", true);

        internal static bool CanMoveAxis(TelescopeAxes Axis)
        {
            LogMessage("CanMoveAxis", "Get - " + Axis.ToString());
            switch (Axis)
            {
                case TelescopeAxes.axisPrimary: return true;
                case TelescopeAxes.axisSecondary: return true;
                case TelescopeAxes.axisTertiary: return false;
                default: throw new InvalidValueException("CanMoveAxis", Axis.ToString(), "0 to 2");
            }
        }

        internal static bool CanPark => LogCapability("CanPark", true);
        internal static bool CanPulseGuide => LogCapability("CanPulseGuide", true);
        internal static bool CanSetDeclinationRate => LogCapability("CanSetDeclinationRate", false);
        internal static bool CanSetGuideRates => LogCapability("CanSetGuideRates", false);
        internal static bool CanSetPark => LogCapability("CanSetPark", false);
        internal static bool CanSetPierSide => LogCapability("CanSetPierSide", false);
        internal static bool CanSetRightAscensionRate => LogCapability("CanSetRightAscensionRate", false);
        internal static bool CanSetTracking => LogCapability("CanSetTracking", true);
        internal static bool CanSlew => LogCapability("CanSlew", true);
        internal static bool CanSlewAltAz => LogCapability("CanSlewAltAz", false);
        internal static bool CanSlewAltAzAsync => LogCapability("CanSlewAltAzAsync", false);
        internal static bool CanSlewAsync => LogCapability("CanSlewAsync", true);
        internal static bool CanSync => LogCapability("CanSync", true);
        internal static bool CanSyncAltAz => LogCapability("CanSyncAltAz", false);
        internal static bool CanUnpark => LogCapability("CanUnpark", true);

        /// <summary>
        /// The declination (degrees) of the telescope's current equatorial coordinates.
        /// </summary>
        internal static double Declination
        {
            get
            {
                double declination = ParseSexagesimal(mount.SendString("GD"), "GD");
                LogMessage("Declination", "Get - " + utilities.DegreesToDMS(declination, ":", ":"));
                return declination;
            }
        }

        /// <summary>
        /// The declination tracking rate (arcseconds per SI second, default = 0.0)
        /// </summary>
        internal static double DeclinationRate
        {
            get
            {
                LogMessage("DeclinationRate", "Get - 0");
                return 0.0;
            }
            set
            {
                LogMessage("DeclinationRate Set", "Not implemented");
                throw new PropertyNotImplementedException("DeclinationRate", true);
            }
        }

        /// <summary>
        /// Predict side of pier for German equatorial mounts at the provided coordinates.
        /// The firmware picks the side with the smaller |axis1| (counterweight lowest), which is
        /// pierEast for hour angles 0..+12h and pierWest for -12..0h.
        /// </summary>
        internal static PierSide DestinationSideOfPier(double RightAscension, double Declination)
        {
            ValidateRaDec("DestinationSideOfPier", RightAscension, Declination);
            double lst = ParseSexagesimal(mount.SendString("GS"), "GS");
            double ha = NormaliseHours(lst - RightAscension);
            PierSide side = ha >= 0 ? PierSide.pierEast : PierSide.pierWest;
            LogMessage("DestinationSideOfPier", $"RA {RightAscension}, Dec {Declination}, HA {ha:F3} => {side}");
            return side;
        }

        internal static bool DoesRefraction
        {
            get
            {
                LogMessage("DoesRefraction Get", "false");
                return false;
            }
            set
            {
                LogMessage("DoesRefraction Set", "Not implemented");
                throw new PropertyNotImplementedException("DoesRefraction", true);
            }
        }

        /// <summary>
        /// Equatorial coordinate system used by this telescope: the firmware works in JNow (topocentric).
        /// </summary>
        internal static EquatorialCoordinateType EquatorialSystem
        {
            get
            {
                LogMessage("EquatorialSystem", "Get - equTopocentric");
                return EquatorialCoordinateType.equTopocentric;
            }
        }

        /// <summary>
        /// Seeks the home switches and then moves to the home position. Synchronous.
        /// </summary>
        internal static void FindHome()
        {
            if (MountAtPark()) throw new ParkedException("FindHome: the mount is parked");
            ClearManualMoves();
            mount.SendBlind("hF");
            Thread.Sleep(200);
            WaitWhileBusy("FindHome");

            // The firmware does not report homing failures over LX200. A successful search ends at
            // the home position (pointing at the pole), so check for that.
            double dec = ParseSexagesimal(mount.SendString("GD"), "GD");
            if (Math.Abs(Math.Abs(dec) - 90.0) > 0.05)
            {
                LogMessage("FindHome", $"Failed - Dec after homing is {dec}");
                throw new DriverException("FindHome: the home search did not finish at the home position. Check the mount's web page log.");
            }
            LogMessage("FindHome", "Completed");
        }

        internal static double FocalLength
        {
            get
            {
                LogMessage("FocalLength Get", "Not implemented");
                throw new PropertyNotImplementedException("FocalLength", false);
            }
        }

        /// <summary>
        /// The Declination guide rate (degrees/sec), as configured in the setup dialogue.
        /// </summary>
        internal static double GuideRateDeclination
        {
            get
            {
                double rate = guideRateSidereal * SiderealDegPerSec;
                LogMessage("GuideRateDeclination Get", rate.ToString(CultureInfo.InvariantCulture));
                return rate;
            }
            set
            {
                LogMessage("GuideRateDeclination Set", "Not implemented");
                throw new PropertyNotImplementedException("GuideRateDeclination", true);
            }
        }

        /// <summary>
        /// The Right Ascension guide rate (degrees/sec), as configured in the setup dialogue.
        /// </summary>
        internal static double GuideRateRightAscension
        {
            get
            {
                double rate = guideRateSidereal * SiderealDegPerSec;
                LogMessage("GuideRateRightAscension Get", rate.ToString(CultureInfo.InvariantCulture));
                return rate;
            }
            set
            {
                LogMessage("GuideRateRightAscension Set", "Not implemented");
                throw new PropertyNotImplementedException("GuideRateRightAscension", true);
            }
        }

        /// <summary>
        /// True if a <see cref="PulseGuide" /> command is in progress. Timed by the driver because
        /// the firmware cannot report it over LX200.
        /// </summary>
        internal static bool IsPulseGuiding
        {
            get
            {
                bool guiding;
                lock (stateLock)
                {
                    DateTime now = DateTime.UtcNow;
                    guiding = now < pulseEnd[0] || now < pulseEnd[1];
                }
                LogMessage("IsPulseGuiding Get", guiding.ToString());
                return guiding;
            }
        }

        /// <summary>
        /// Move the telescope in one axis at the given rate. Only the four firmware rates are accepted.
        /// Primary axis: positive = RA increasing (east). Secondary axis: positive = north.
        /// </summary>
        internal static void MoveAxis(TelescopeAxes Axis, double Rate)
        {
            LogMessage("MoveAxis", $"Axis {Axis}, rate {Rate}");
            if (Axis != TelescopeAxes.axisPrimary && Axis != TelescopeAxes.axisSecondary)
                throw new InvalidValueException("MoveAxis", Axis.ToString(), "axisPrimary or axisSecondary");

            int axis = Axis == TelescopeAxes.axisPrimary ? 0 : 1;

            if (Rate == 0.0)
            {
                mount.SendBlind(axis == 0 ? "Qe" : "Qn");
                lock (stateLock) { axisMoving[axis] = false; }
                return;
            }

            if (MountAtPark()) throw new ParkedException("MoveAxis: the mount is parked");
            if (MountBusy()) throw new ASCOM.InvalidOperationException("MoveAxis: the mount is slewing");

            char rateCode = MoveRateCode(Math.Abs(Rate));
            if (rateCode == '\0')
                throw new InvalidValueException("MoveAxis", Rate.ToString(CultureInfo.InvariantCulture), AxisRatesDescription());

            string dir = axis == 0 ? (Rate > 0 ? "e" : "w") : (Rate > 0 ? "n" : "s");
            mount.SendBlind("R" + rateCode);
            mount.SendBlind("M" + dir);
            lock (stateLock) { axisMoving[axis] = true; }
        }

        /// <summary>
        /// Move the telescope to its park position (the home position), stop tracking and set AtPark. Synchronous.
        /// </summary>
        internal static void Park()
        {
            if (MountAtPark())
            {
                LogMessage("Park", "Already parked");
                return;
            }
            ClearManualMoves();
            mount.SendBlind("hP");
            Thread.Sleep(200);
            WaitWhileBusy("Park");
            if (!MountAtPark()) throw new DriverException("Park: the mount did not report parked after the park slew.");
            LogMessage("Park", "Completed");
        }

        /// <summary>
        /// Moves the scope in the given direction for the given interval at the guide rate. Asynchronous.
        /// </summary>
        internal static void PulseGuide(GuideDirections Direction, int Duration)
        {
            LogMessage("PulseGuide", $"Direction {Direction}, duration {Duration} ms");
            if (Duration < 0 || Duration > MaxPulseMs)
                throw new InvalidValueException("PulseGuide", Duration.ToString(CultureInfo.InvariantCulture), $"0 to {MaxPulseMs}");

            char dir;
            int axis;
            switch (Direction)
            {
                case GuideDirections.guideNorth: dir = 'n'; axis = 1; break;
                case GuideDirections.guideSouth: dir = 's'; axis = 1; break;
                case GuideDirections.guideEast: dir = 'e'; axis = 0; break;
                case GuideDirections.guideWest: dir = 'w'; axis = 0; break;
                default: throw new InvalidValueException("PulseGuide", Direction.ToString(), "guideNorth, guideSouth, guideEast or guideWest");
            }

            if (MountAtPark()) throw new ParkedException("PulseGuide: the mount is parked");
            if (MountBusy()) throw new ASCOM.InvalidOperationException("PulseGuide: the mount is slewing");

            if (Duration == 0) return;
            mount.SendBlind($"Mg{dir}{Duration:0000}");
            lock (stateLock) { pulseEnd[axis] = DateTime.UtcNow.AddMilliseconds(Duration); }
        }

        /// <summary>
        /// The right ascension (hours) of the telescope's current equatorial coordinates.
        /// </summary>
        internal static double RightAscension
        {
            get
            {
                double rightAscension = ParseSexagesimal(mount.SendString("GR"), "GR");
                LogMessage("RightAscension", "Get - " + utilities.HoursToHMS(rightAscension));
                return rightAscension;
            }
        }

        internal static double RightAscensionRate
        {
            get
            {
                LogMessage("RightAscensionRate", "Get - 0");
                return 0.0;
            }
            set
            {
                LogMessage("RightAscensionRate Set", "Not implemented");
                throw new PropertyNotImplementedException("RightAscensionRate", true);
            }
        }

        internal static void SetPark()
        {
            LogMessage("SetPark", "Not implemented");
            throw new MethodNotImplementedException("SetPark");
        }

        /// <summary>
        /// The pointing state of the mount, from :Gm#.
        /// </summary>
        internal static PierSide SideOfPier
        {
            get
            {
                string reply = mount.SendString("Gm");
                PierSide side;
                switch (reply.Trim())
                {
                    case "E": side = PierSide.pierEast; break;
                    case "W": side = PierSide.pierWest; break;
                    default: side = PierSide.pierUnknown; break;
                }
                LogMessage("SideOfPier Get", side.ToString());
                return side;
            }
            set
            {
                LogMessage("SideOfPier Set", "Not implemented");
                throw new PropertyNotImplementedException("SideOfPier", true);
            }
        }

        /// <summary>
        /// The local apparent sidereal time from the mount's clock (hours).
        /// </summary>
        internal static double SiderealTime
        {
            get
            {
                double siderealTime = ParseSexagesimal(mount.SendString("GS"), "GS");
                LogMessage("SiderealTime", "Get - " + siderealTime.ToString(CultureInfo.InvariantCulture));
                return siderealTime;
            }
        }

        internal static double SiteElevation
        {
            get
            {
                LogMessage("SiteElevation Get", "Not implemented");
                throw new PropertyNotImplementedException("SiteElevation", false);
            }
            set
            {
                LogMessage("SiteElevation Set", "Not implemented");
                throw new PropertyNotImplementedException("SiteElevation", true);
            }
        }

        /// <summary>
        /// Site latitude (degrees, positive North). The mount reports whole arcminutes only, so the
        /// last value written is returned if it agrees with the mount to within that resolution.
        /// </summary>
        internal static double SiteLatitude
        {
            get
            {
                double lat = ParseSexagesimal(mount.SendString("Gt"), "Gt");
                lock (stateLock)
                {
                    if (lastSetLatitude.HasValue && Math.Abs(lastSetLatitude.Value - lat) <= 1.0 / 60.0)
                        lat = lastSetLatitude.Value;
                }
                LogMessage("SiteLatitude Get", lat.ToString(CultureInfo.InvariantCulture));
                return lat;
            }
            set
            {
                LogMessage("SiteLatitude Set", value.ToString(CultureInfo.InvariantCulture));
                if (double.IsNaN(value) || value < -90.0 || value > 90.0)
                    throw new InvalidValueException("SiteLatitude", value.ToString(CultureInfo.InvariantCulture), "-90 to +90");
                // The firmware's parser accepts seconds after the standard sDD*MM.
                if (mount.SendChar("St" + FormatSignedDms(value, 2)) != '1')
                    throw new DriverException("SiteLatitude: the mount rejected the latitude.");
                lock (stateLock) { lastSetLatitude = value; }
            }
        }

        /// <summary>
        /// Site longitude (degrees, positive East). LX200 uses west-positive 0..360, converted here.
        /// </summary>
        internal static double SiteLongitude
        {
            get
            {
                double west = ParseSexagesimal(mount.SendString("Gg"), "Gg");
                double lon = NormaliseDegrees180(-west);
                lock (stateLock)
                {
                    if (lastSetLongitude.HasValue && Math.Abs(NormaliseDegrees180(lastSetLongitude.Value - lon)) <= 1.0 / 60.0)
                        lon = lastSetLongitude.Value;
                }
                LogMessage("SiteLongitude Get", lon.ToString(CultureInfo.InvariantCulture));
                return lon;
            }
            set
            {
                LogMessage("SiteLongitude Set", value.ToString(CultureInfo.InvariantCulture));
                if (double.IsNaN(value) || value < -180.0 || value > 180.0)
                    throw new InvalidValueException("SiteLongitude", value.ToString(CultureInfo.InvariantCulture), "-180 to +180");
                double west = -value;
                if (west < 0) west += 360.0;
                if (mount.SendChar("Sg" + FormatDms(west, 3)) != '1')
                    throw new DriverException("SiteLongitude: the mount rejected the longitude.");
                lock (stateLock) { lastSetLongitude = value; }
            }
        }

        /// <summary>
        /// Post-slew settling time (sec.), applied by the driver after synchronous slews.
        /// </summary>
        internal static short SlewSettleTime
        {
            get
            {
                LogMessage("SlewSettleTime Get", slewSettleTime.ToString());
                return slewSettleTime;
            }
            set
            {
                LogMessage("SlewSettleTime Set", value.ToString());
                if (value < 0) throw new InvalidValueException("SlewSettleTime", value.ToString(), "0 or more");
                slewSettleTime = value;
            }
        }

        internal static void SlewToAltAz(double Azimuth, double Altitude)
        {
            LogMessage("SlewToAltAz", "Not implemented");
            throw new MethodNotImplementedException("SlewToAltAz");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "internal static method name used for many years.")]
        internal static void SlewToAltAzAsync(double Azimuth, double Altitude)
        {
            LogMessage("SlewToAltAzAsync", "Not implemented");
            throw new MethodNotImplementedException("SlewToAltAzAsync");
        }

        /// <summary>
        /// Slews to the given coordinates and returns when the slew is complete.
        /// </summary>
        internal static void SlewToCoordinates(double RightAscension, double Declination)
        {
            LogMessage("SlewToCoordinates", $"RA {RightAscension}, Dec {Declination}");
            SlewToCoordinatesAsync(RightAscension, Declination);
            WaitForSlew("SlewToCoordinates");
        }

        /// <summary>
        /// Starts a slew to the given coordinates and returns immediately.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "internal static method name used for many years.")]
        internal static void SlewToCoordinatesAsync(double RightAscension, double Declination)
        {
            LogMessage("SlewToCoordinatesAsync", $"RA {RightAscension}, Dec {Declination}");
            ValidateRaDec("SlewToCoordinatesAsync", RightAscension, Declination);
            if (MountAtPark()) throw new ParkedException("SlewToCoordinatesAsync: the mount is parked");
            SendTarget(RightAscension, Declination);
            StartGoto();
        }

        internal static void SlewToTarget()
        {
            LogMessage("SlewToTarget", "Start");
            SlewToTargetAsync();
            WaitForSlew("SlewToTarget");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "internal static method name used for many years.")]
        internal static void SlewToTargetAsync()
        {
            double ra, dec;
            GetTarget("SlewToTargetAsync", out ra, out dec);
            if (MountAtPark()) throw new ParkedException("SlewToTargetAsync: the mount is parked");
            SendTarget(ra, dec);
            StartGoto();
        }

        /// <summary>
        /// True while a goto, park, home search or MoveAxis motion is in progress.
        /// </summary>
        internal static bool Slewing
        {
            get
            {
                bool slewing = AnyAxisMoving() || MountBusy();
                LogMessage("Slewing Get", slewing.ToString());
                return slewing;
            }
        }

        internal static void SyncToAltAz(double Azimuth, double Altitude)
        {
            LogMessage("SyncToAltAz", "Not implemented");
            throw new MethodNotImplementedException("SyncToAltAz");
        }

        internal static void SyncToCoordinates(double RightAscension, double Declination)
        {
            LogMessage("SyncToCoordinates", $"RA {RightAscension}, Dec {Declination}");
            ValidateRaDec("SyncToCoordinates", RightAscension, Declination);
            DoSync(RightAscension, Declination);
        }

        internal static void SyncToTarget()
        {
            double ra, dec;
            GetTarget("SyncToTarget", out ra, out dec);
            DoSync(ra, dec);
        }

        /// <summary>
        /// The declination (degrees, positive North) for the target of an equatorial slew or sync operation
        /// </summary>
        internal static double TargetDeclination
        {
            get
            {
                lock (stateLock)
                {
                    if (!targetDec.HasValue) throw new ASCOM.InvalidOperationException("TargetDeclination has not been set");
                    LogMessage("TargetDeclination Get", targetDec.Value.ToString(CultureInfo.InvariantCulture));
                    return targetDec.Value;
                }
            }
            set
            {
                LogMessage("TargetDeclination Set", value.ToString(CultureInfo.InvariantCulture));
                if (double.IsNaN(value) || value < -90.0 || value > 90.0)
                    throw new InvalidValueException("TargetDeclination", value.ToString(CultureInfo.InvariantCulture), "-90 to +90");
                if (mount.SendChar("Sd" + FormatSignedDms(value, 2)) != '1')
                    throw new InvalidValueException("TargetDeclination", value.ToString(CultureInfo.InvariantCulture), "rejected by the mount");
                lock (stateLock) { targetDec = value; }
            }
        }

        /// <summary>
        /// The right ascension (hours) for the target of an equatorial slew or sync operation
        /// </summary>
        internal static double TargetRightAscension
        {
            get
            {
                lock (stateLock)
                {
                    if (!targetRa.HasValue) throw new ASCOM.InvalidOperationException("TargetRightAscension has not been set");
                    LogMessage("TargetRightAscension Get", targetRa.Value.ToString(CultureInfo.InvariantCulture));
                    return targetRa.Value;
                }
            }
            set
            {
                LogMessage("TargetRightAscension Set", value.ToString(CultureInfo.InvariantCulture));
                if (double.IsNaN(value) || value < 0.0 || value >= 24.0)
                    throw new InvalidValueException("TargetRightAscension", value.ToString(CultureInfo.InvariantCulture), "0 to 24");
                if (mount.SendChar("Sr" + FormatHms(value)) != '1')
                    throw new InvalidValueException("TargetRightAscension", value.ToString(CultureInfo.InvariantCulture), "rejected by the mount");
                lock (stateLock) { targetRa = value; }
            }
        }

        /// <summary>
        /// The state of the telescope's tracking drive, from :GW#.
        /// </summary>
        internal static bool Tracking
        {
            get
            {
                string status = mount.SendString("GW");
                bool tracking = status.Length > 1 && status[1] == 'T';
                LogMessage("Tracking", "Get - " + tracking.ToString());
                return tracking;
            }
            set
            {
                LogMessage("Tracking Set", value.ToString());
                if (value)
                {
                    if (mount.SendChar("Te") != '1')
                    {
                        if (MountAtPark()) throw new ParkedException("Tracking: the mount is parked");
                        throw new DriverException("Tracking: the mount refused to start tracking");
                    }
                }
                else
                {
                    mount.SendChar("Td");
                }
            }
        }

        /// <summary>
        /// The current tracking rate. The firmware cannot report it, so the last value set is returned
        /// (sidereal is set on connection).
        /// </summary>
        internal static DriveRates TrackingRate
        {
            get
            {
                LogMessage("TrackingRate Get", $"{trackingRate}");
                return trackingRate;
            }
            set
            {
                LogMessage("TrackingRate Set", $"{value}");
                switch (value)
                {
                    case DriveRates.driveSidereal: mount.SendBlind("TQ"); break;
                    case DriveRates.driveLunar: mount.SendBlind("TL"); break;
                    case DriveRates.driveSolar: mount.SendBlind("TS"); break;
                    default: throw new InvalidValueException("TrackingRate", value.ToString(), "driveSidereal, driveLunar or driveSolar");
                }
                trackingRate = value;
            }
        }

        /// <summary>
        /// Returns the supported tracking rates.
        /// </summary>
        internal static ITrackingRates TrackingRates
        {
            get
            {
                ITrackingRates trackingRates = new TrackingRates();
                LogMessage("TrackingRates", "Get - ");
                foreach (DriveRates driveRate in trackingRates)
                {
                    LogMessage("TrackingRates", "Get - " + driveRate.ToString());
                }
                return trackingRates;
            }
        }

        /// <summary>
        /// The UTC date/time of the mount's clock. LX200 exposes local date and time plus the
        /// UTC offset (hours to add to local time to get UTC).
        /// </summary>
        internal static DateTime UTCDate
        {
            get
            {
                double offset = ParseUtcOffset(mount.SendString("GG"));
                string date = mount.SendString("GC");
                string time = mount.SendString("GL");
                string date2 = mount.SendString("GC");
                if (date2 != date)
                {
                    // Midnight passed between the reads
                    date = date2;
                    time = mount.SendString("GL");
                }
                DateTime local = ParseLx200DateTime(date, time);
                DateTime utc = DateTime.SpecifyKind(local.AddHours(offset), DateTimeKind.Utc);
                LogMessage("UTCDate", "Get - " + utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                return utc;
            }
            set
            {
                LogMessage("UTCDate Set", value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
                SetMountUtc(utc);
            }
        }

        /// <summary>
        /// Takes telescope out of the Parked state.
        /// </summary>
        internal static void Unpark()
        {
            char reply = mount.SendChar("hR");
            LogMessage("Unpark", $"Reply {reply}");
            if (MountAtPark()) throw new DriverException("Unpark: the mount is still parked");
        }

        #endregion

        #region Private properties and methods

        /// <summary>
        /// The four manual move rates in deg/s, in ascending order, with their LX200 rate codes.
        /// </summary>
        internal static KeyValuePair<char, double>[] MoveRates
        {
            get
            {
                return new[]
                {
                    new KeyValuePair<char, double>('G', guideRateSidereal * SiderealDegPerSec),
                    new KeyValuePair<char, double>('C', CenterRateSidereal * SiderealDegPerSec),
                    new KeyValuePair<char, double>('M', slewRateDegPerSec * 0.25),
                    new KeyValuePair<char, double>('S', slewRateDegPerSec),
                };
            }
        }

        private static char MoveRateCode(double rate)
        {
            foreach (var r in MoveRates)
            {
                if (Math.Abs(rate - r.Value) <= r.Value * 0.01) return r.Key;
            }
            return '\0';
        }

        private static string AxisRatesDescription()
        {
            var parts = new List<string>();
            foreach (var r in MoveRates) parts.Add(r.Value.ToString("0.#####", CultureInfo.InvariantCulture));
            return "+/- one of " + string.Join(", ", parts) + " deg/s";
        }

        private static bool LogCapability(string name, bool value)
        {
            LogMessage(name, "Get - " + value.ToString());
            return value;
        }

        /// <summary>True while the firmware runs a goto, park or home search (:D# returns 0x7F).</summary>
        private static bool MountBusy()
        {
            return mount.SendString("D").IndexOf('\x7f') >= 0;
        }

        private static bool MountAtPark()
        {
            string status = mount.SendString("GW");
            return status.Length > 2 && status[2] == 'P';
        }

        private static bool AnyAxisMoving()
        {
            lock (stateLock) { return axisMoving[0] || axisMoving[1]; }
        }

        private static void ClearManualMoves()
        {
            lock (stateLock) { axisMoving[0] = axisMoving[1] = false; }
        }

        private static void WaitWhileBusy(string caller)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (MountBusy())
            {
                if (timer.ElapsedMilliseconds > SlewTimeoutMs)
                {
                    mount.SendBlind("Q");
                    throw new DriverException($"{caller}: timed out waiting for the mount to stop moving.");
                }
                Thread.Sleep(PollIntervalMs);
            }
        }

        private static void WaitForSlew(string caller)
        {
            Thread.Sleep(200);
            WaitWhileBusy(caller);
            if (slewSettleTime > 0) Thread.Sleep(slewSettleTime * 1000);
            LogMessage(caller, "Slew complete");
        }

        private static void ValidateRaDec(string caller, double ra, double dec)
        {
            if (double.IsNaN(ra) || ra < 0.0 || ra >= 24.0)
                throw new InvalidValueException(caller, ra.ToString(CultureInfo.InvariantCulture), "RA 0 to 24 hours");
            if (double.IsNaN(dec) || dec < -90.0 || dec > 90.0)
                throw new InvalidValueException(caller, dec.ToString(CultureInfo.InvariantCulture), "Dec -90 to +90 degrees");
        }

        private static void GetTarget(string caller, out double ra, out double dec)
        {
            lock (stateLock)
            {
                if (!targetRa.HasValue || !targetDec.HasValue)
                    throw new ASCOM.InvalidOperationException($"{caller}: TargetRightAscension and TargetDeclination must both be set");
                ra = targetRa.Value;
                dec = targetDec.Value;
            }
        }

        private static void SendTarget(double ra, double dec)
        {
            TargetRightAscension = ra;
            TargetDeclination = dec;
        }

        private static void StartGoto()
        {
            if (MountBusy() && !AnyAxisMoving())
            {
                // The firmware allows a goto to replace another goto, but not a park or home search.
                LogMessage("StartGoto", "Mount busy; the firmware will refuse if this is a park or home search");
            }
            ClearManualMoves();
            char status = mount.SendCharWithText("MS", c => c != '0', 1, out string text);
            if (status != '0')
            {
                string reason = text.Trim().TrimEnd('.');
                if (reason.Length == 0) reason = "the mount refused the slew";
                throw new ASCOM.InvalidOperationException($"Slew refused: {reason}");
            }
        }

        private static void DoSync(double ra, double dec)
        {
            if (MountAtPark()) throw new ParkedException("Sync: the mount is parked");
            if (MountBusy()) throw new ASCOM.InvalidOperationException("Sync: the mount is slewing");
            SendTarget(ra, dec);
            string reply = mount.SendString("CM");
            LogMessage("Sync", $"Reply: {reply}");
        }

        private static void SetMountUtc(DateTime utc)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            double offset = ParseUtcOffset(mount.SendString("GG"));
            DateTime local = utc.AddHours(-offset);

            // Date first, then time: :SC keeps the time of day and :SL keeps the day.
            // :SC replies "1Updating Planetary Data#<spaces>#" on success, "0" on failure.
            string dateCmd = "SC" + local.ToString("MM/dd/yy", CultureInfo.InvariantCulture);
            char dateReply = mount.SendCharWithText(dateCmd, c => c == '1', 2, out _);
            if (dateReply != '1') throw new DriverException($"UTCDate: the mount rejected the date ({dateCmd}).");

            // Allow for the time the serial exchanges took (only matters if this crosses midnight
            // by a second or so, in which case the day set above is very slightly stale).
            local = local.Add(elapsed.Elapsed);
            string timeCmd = "SL" + local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (mount.SendChar(timeCmd) != '1') throw new DriverException($"UTCDate: the mount rejected the time ({timeCmd}).");
        }

        private static double ParseUtcOffset(string text)
        {
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
                throw new DriverException($"Invalid UTC offset from mount: \"{text}\"");
            return offset;
        }

        private static DateTime ParseLx200DateTime(string date, string time)
        {
            string[] d = date.Trim().Split('/');
            if (d.Length != 3 ||
                !int.TryParse(d[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int month) ||
                !int.TryParse(d[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day) ||
                !int.TryParse(d[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
                throw new DriverException($"Invalid date from mount: \"{date}\"");
            if (year < 100) year += 2000;
            double hours = ParseSexagesimal(time, "GL");
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified).AddSeconds(Math.Round(hours * 3600.0));
        }

        /// <summary>
        /// Parses the firmware's sexagesimal formats (HH:MM:SS, sDD*MM'SS, sDD*MM, DDD*MM'SS):
        /// an optional sign and up to three numbers separated by any non-digit characters.
        /// </summary>
        internal static double ParseSexagesimal(string text, string command)
        {
            string s = text.Trim();
            bool negative = false;
            int i = 0;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                negative = s[i] == '-';
                i++;
            }

            double[] parts = new double[3];
            int n = 0;
            while (n < 3 && i < s.Length)
            {
                while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '.') i++;
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i == start) break;
                if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out parts[n]))
                    break;
                n++;
            }

            if (n == 0) throw new DriverException($"Invalid reply to :{command}#: \"{text}\"");
            double value = parts[0] + parts[1] / 60.0 + parts[2] / 3600.0;
            return negative ? -value : value;
        }

        private static void SplitDms(double value, out int d, out int m, out int s)
        {
            long total = (long)Math.Round(Math.Abs(value) * 3600.0);
            d = (int)(total / 3600);
            m = (int)(total % 3600 / 60);
            s = (int)(total % 60);
        }

        private static string FormatHms(double hours)
        {
            SplitDms(hours, out int h, out int m, out int s);
            return $"{h % 24:00}:{m:00}:{s:00}";
        }

        private static string FormatSignedDms(double degrees, int degreeDigits)
        {
            SplitDms(degrees, out int d, out int m, out int s);
            string sign = degrees < 0 && (d | m | s) != 0 ? "-" : "+";
            return sign + d.ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture) + $"*{m:00}:{s:00}";
        }

        private static string FormatDms(double degrees, int degreeDigits)
        {
            SplitDms(degrees, out int d, out int m, out int s);
            return (d % 360).ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture) + $"*{m:00}:{s:00}";
        }

        private static double NormaliseHours(double hours)
        {
            hours %= 24.0;
            if (hours >= 12.0) hours -= 24.0;
            if (hours < -12.0) hours += 24.0;
            return hours;
        }

        private static double NormaliseDegrees180(double degrees)
        {
            degrees %= 360.0;
            if (degrees > 180.0) degrees -= 360.0;
            if (degrees <= -180.0) degrees += 360.0;
            return degrees;
        }

        private static string Unframe(string command)
        {
            string c = command.Trim();
            if (c.StartsWith(":")) c = c.Substring(1);
            if (c.EndsWith("#")) c = c.Substring(0, c.Length - 1);
            return c;
        }

        /// <summary>
        /// Returns true if there is a valid connection to the driver hardware
        /// </summary>
        private static bool IsConnected
        {
            get
            {
                return connectedState && mount != null && mount.IsOpen;
            }
        }

        /// <summary>
        /// Use this function to throw an exception if we aren't connected to the hardware
        /// </summary>
        private static void CheckConnected(string message)
        {
            if (!IsConnected)
            {
                throw new NotConnectedException(message);
            }
        }

        /// <summary>
        /// Read the device configuration from the ASCOM Profile store
        /// </summary>
        internal static void ReadProfile()
        {
            using (Profile driverProfile = new Profile())
            {
                driverProfile.DeviceType = "Telescope";
                tl.Enabled = Convert.ToBoolean(driverProfile.GetValue(DriverProgId, traceStateProfileName, string.Empty, traceStateDefault));
                comPort = driverProfile.GetValue(DriverProgId, comPortProfileName, string.Empty, comPortDefault);
                guideRateSidereal = ReadDouble(driverProfile, guideRateProfileName, guideRateDefault);
                slewRateDegPerSec = ReadDouble(driverProfile, slewRateProfileName, slewRateDefault);
                syncTimeOnConnect = Convert.ToBoolean(driverProfile.GetValue(DriverProgId, syncTimeProfileName, string.Empty, syncTimeDefault));
            }
        }

        private static double ReadDouble(Profile profile, string name, string defaultValue)
        {
            string text = profile.GetValue(DriverProgId, name, string.Empty, defaultValue);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
                value = double.Parse(defaultValue, CultureInfo.InvariantCulture);
            return value;
        }

        /// <summary>
        /// Write the device configuration to the  ASCOM  Profile store
        /// </summary>
        internal static void WriteProfile()
        {
            using (Profile driverProfile = new Profile())
            {
                driverProfile.DeviceType = "Telescope";
                driverProfile.WriteValue(DriverProgId, traceStateProfileName, tl.Enabled.ToString());
                driverProfile.WriteValue(DriverProgId, comPortProfileName, comPort.ToString());
                driverProfile.WriteValue(DriverProgId, guideRateProfileName, guideRateSidereal.ToString(CultureInfo.InvariantCulture));
                driverProfile.WriteValue(DriverProgId, slewRateProfileName, slewRateDegPerSec.ToString(CultureInfo.InvariantCulture));
                driverProfile.WriteValue(DriverProgId, syncTimeProfileName, syncTimeOnConnect.ToString());
            }
        }

        /// <summary>
        /// Log helper function that takes identifier and message strings
        /// </summary>
        internal static void LogMessage(string identifier, string message)
        {
            tl?.LogMessageCrLf(identifier, message);
        }

        /// <summary>
        /// Log helper function that takes formatted strings and arguments
        /// </summary>
        internal static void LogMessage(string identifier, string message, params object[] args)
        {
            var msg = string.Format(message, args);
            LogMessage(identifier, msg);
        }
        #endregion
    }
}

using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Writes a fake flight in DJI Airdata CSV format (same columns FlightLog reads),
// so the replay works without a real flight log. python/telemetry_sender.py can stream it too.
public static class MakeTestFlight
{
    const double FT = 0.3048, MPH = 0.44704;

    [MenuItem("Tools/Generate Fake Flight CSV")]
    public static void Make()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("time(millisecond),latitude,longitude,height_above_takeoff(feet),speed(mph),compass_heading(degrees),pitch(degrees),roll(degrees),battery_percent,satellites,flycState,message");

        double lat0 = 36.8969, lon0 = 30.7133; // near Antalya
        double r = 40.0;      // radius (m)
        double lap = 120.0;   // one lap (s)
        double duration = 900; // covers the sample times in detections.json
        double speed = 2 * System.Math.PI * r / lap;
        for (int i = 0; i <= duration * 2; i++) // 2 Hz; FlightLog interpolates
        {
            double t = i / 2.0;
            double a = t / lap * 2 * System.Math.PI;
            double east = r * System.Math.Sin(a);
            double north = r * (1 - System.Math.Cos(a));
            double lat = lat0 + north / 111320.0;
            double lon = lon0 + east / (111320.0 * System.Math.Cos(lat0 * System.Math.PI / 180));
            double h = Mathf.Min((float)t * 2f, 20f) + 3 * System.Math.Sin(a * 3);
            double heading = (a * 180 / System.Math.PI + 90) % 360; // tangent direction
            double battery = 100 - 70 * t / duration;

            sb.AppendLine(string.Join(",",
                ((long)(t * 1000)).ToString(ci), lat.ToString("F7", ci), lon.ToString("F7", ci),
                (h / FT).ToString("F2", ci), (speed / MPH).ToString("F2", ci), heading.ToString("F1", ci),
                "-8.0", "15.0", ((int)battery).ToString(ci), "15", "P-GPS", ""));
        }
        File.WriteAllText("Assets/Data/test_flight.csv", sb.ToString());
        AssetDatabase.Refresh();
        Debug.Log("Assets/Data/test_flight.csv created");
    }
}

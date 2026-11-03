using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// One telemetry sample (all values converted to metric).
public struct Sample {
    public float t;            //seconds since start
    public Vector3 pos;        //Unity metres: x=east, y=up, z=north
    public double lat, lon;    //WGS84, for the map
    public float heading, pitch, roll;
    public float speed;        // m/s
    public float battery;      // %
    public int sats;
    public string mode, message;
    public float gimbalPitch, gimbalHeading;  //degrees (pitch -90 = straight down); NaN if not in log
    public bool recording;     //camera is recording video
}

// Reads two CSV messages, detected from the header:
//  - Airdata export:            time(millisecond), latitude, height_above_takeoff(feet), ...
//  - DJI flight record viewer:  OSD.flyTime [s], OSD.latitude, OSD.height [ft], ... (one *-aircraft.csv per flight)
public static class FlightLog {
    const float FT = 0.3048f, MPH = 0.44704f;

    class Cols {
        public int t, lat, lon, h, sp, hd, pi, ro, bat, sat, mode, gPi = -1, gHd = -1, rec = -1;
        public int[] msg;          //first non-empty wins
        public float tScale;       //file time unit -> seconds
    }

    public static List<Sample> Load(string path) {
        var lines = File.ReadAllLines(path);
        var h = SplitCsv(lines[0]).ConvertAll(s => s.Trim().TrimStart('﻿'));
        int Col(string name) {
            int i = h.IndexOf(name);
            if (i < 0) throw new Exception("Column not found: " + name);
            return i;
        }
        int Opt(string name) => h.IndexOf(name);

        Cols c;
        if (h.Contains("OSD.flyTime [s]")) {
            c = new Cols {
                t = Col("OSD.flyTime [s]"), tScale = 1f,
                lat = Col("OSD.latitude"), lon = Col("OSD.longitude"), h = Col("OSD.height [ft]"),
                sp = Col("OSD.hSpeed [mph]"), hd = Col("OSD.yaw [360]"), pi = Col("OSD.pitch"), ro = Col("OSD.roll"),
                bat = Col("BATTERY.chargeLevel [%]"), sat = Col("OSD.gpsNum"), mode = Col("OSD.flycState"),
                gPi = Opt("GIMBAL.pitch"), gHd = Opt("GIMBAL.yaw [360]"), rec = Opt("CAMERA.isVideo"),
                msg = new[] { Opt("APP.warning"), Opt("APP.tip") }
            };
        } else {
            c = new Cols {
                t = Col("time(millisecond)"), tScale = 0.001f,
                lat = Col("latitude"), lon = Col("longitude"), h = Col("height_above_takeoff(feet)"),
                sp = Col("speed(mph)"), hd = Col("compass_heading(degrees)"), pi = Col("pitch(degrees)"),
                ro = Col("roll(degrees)"), bat = Col("battery_percent"), sat = Col("satellites"), mode = Col("flycState"),
                gPi = Opt("gimbal_pitch(degrees)"), gHd = Opt("gimbal_heading(degrees)"), rec = Opt("isVideo"),
                msg = new[] { h.Count - 1 }  // message is last, may contain quoted commas
            };
        }

        var list = new List<Sample>();
        double lat0 = 0, lon0 = 0, t0 = 0; bool first = true;
        float lastBat = float.NaN;
        var ci = CultureInfo.InvariantCulture;
        for (int i = 1; i < lines.Length; i++) {
            var r = SplitCsv(lines[i]);
            if (r.Count <= c.mode) continue;
            string S(int k) => k >= 0 && k < r.Count ? r[k].Trim() : "";
            float F(int k, float def) => float.TryParse(S(k), NumberStyles.Float, ci, out var v) ? v : def;

            if (!double.TryParse(S(c.lat), NumberStyles.Float, ci, out var lat) ||
                !double.TryParse(S(c.lon), NumberStyles.Float, ci, out var lon) || (lat == 0 && lon == 0)) continue;
            double t = F(c.t, 0) * c.tScale;
            if (first) { lat0 = lat; lon0 = lon; t0 = t; first = false; }
            var en = ToLocal(lat, lon, lat0, lon0);
            lastBat = F(c.bat, lastBat);  // battery is blank for the first rows of DJI logs

            string msg = "";
            foreach (int k in c.msg) if (S(k) != "") { msg = CleanText(S(k)); break; }
            string rec = S(c.rec).ToLowerInvariant();

            list.Add(new Sample {
                t = (float)(t - t0),
                pos = new Vector3(en.x, F(c.h, 0) * FT, en.y),
                lat = lat, lon = lon,
                heading = F(c.hd, 0), pitch = F(c.pi, 0), roll = F(c.ro, 0),
                speed = F(c.sp, 0) * MPH,
                battery = lastBat,
                sats = (int)F(c.sat, 0),
                mode = S(c.mode),
                message = msg,
                gimbalPitch = F(c.gPi, float.NaN), gimbalHeading = F(c.gHd, float.NaN),
                recording = rec == "true" || rec == "1"
            });
        }
        // back-fill the leading blank battery rows with the first known value
        int firstBat = list.FindIndex(s => !float.IsNaN(s.battery));
        for (int i = 0; i < firstBat; i++) { var s = list[i]; s.battery = list[firstBat].battery; list[i] = s; }
        return list;
    }

    //Metres east (x) and north (y) of the origin. Equirectangular approx, fine for < few km.
    // The first sample of a log is its origin, so log[0].lat/lon map to (0, 0).
    public static Vector2 ToLocal(double lat, double lon, double lat0, double lon0) =>
        new Vector2((float)((lon - lon0) * 111320.0 * Math.Cos(lat0 * Math.PI / 180)),
                    (float)((lat - lat0) * 111320.0));

    // Inverse of ToLocal: metres east (x) / north (y) of the origin -> lat, lon.
    public static (double lat, double lon) FromLocal(Vector2 en, double lat0, double lon0) =>
        (lat0 + en.y / 111320.0, lon0 + en.x / (111320.0 * Math.Cos(lat0 * Math.PI / 180)));

    // DJI app messages carry stray bytes (U+FFFD, control chars) before the final '.'
    static string CleanText(string s) {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s) if (ch != '�' && !char.IsControl(ch)) sb.Append(ch);
        return sb.ToString();
    }

    // minimal CSV splitter honoring double quotes
    static List<string> SplitCsv(string s) {
        var r = new List<string>(); var sb = new System.Text.StringBuilder(); bool q = false;
        foreach (char ch in s) {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { r.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        r.Add(sb.ToString()); return r;
    }
}

using System.CommandLine;
using System.Globalization;
using Panelith.Cli.Commands;
using Panelith.Devices;

namespace Panelith.Cli;

/// <summary>Entry point for the <c>panelith</c> command.</summary>
public static class Program
{
    /// <summary>Parses arguments and runs the selected command.</summary>
    public static Task<int> Main(string[] args)
    {
        Hosting.ProcessHardening.RestrictDllSearch();

        var verbose = new Option<bool>("--verbose", "-v")
        {
            Description = "Print discovery and sensor diagnostics.",
            Recursive = true,
        };

        var root = new RootCommand(
            "Panelith drives USB sensor panels — Thermalright Trofeo Vision, Turing Smart Screens, and others — from one theme.")
        {
            verbose,
        };

        root.Subcommands.Add(DevicesCommand(verbose));
        root.Subcommands.Add(RunCommand(verbose));
        root.Subcommands.Add(PreviewCommand(verbose));
        root.Subcommands.Add(DoctorCommand(verbose));
        root.Subcommands.Add(ProfilesCommand(verbose));
        root.Subcommands.Add(ThemeCommand(verbose));
        root.Subcommands.Add(SensorsCommand(verbose));

        return root.Parse(args).InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
    }

    private static Command DevicesCommand(Option<bool> verbose)
    {
        var demo = new Option<bool>("--demo")
        {
            Description = "Also start simulated sensors. Does not affect which panels are listed.",
        };
        var includeVirtual = new Option<bool>("--virtual")
        {
            Description = "Include the built-in virtual panel.",
        };

        var command = new Command("devices", "List panels that are plugged in right now.")
        {
            demo,
            includeVirtual,
        };
        command.SetAction((parse, cancellation) =>
            CliActions.DevicesAsync(
                parse.GetValue(verbose),
                parse.GetValue(demo),
                parse.GetValue(includeVirtual),
                cancellation));
        return command;
    }

    private static Command RunCommand(Option<bool> verbose)
    {
        var theme = new Option<string>("--theme", "-t")
        {
            Description = "Theme id or path. Defaults to the theme in panelith.json (\"rig\").",
        };
        var demo = new Option<bool>("--demo")
        {
            Description = "Drive the panel from simulated sensors instead of this machine's.",
        };
        var includeVirtual = new Option<bool>("--virtual")
        {
            Description = "Also drive the built-in virtual panel. Writes frame.png when --output is set.",
        };
        var output = new Option<string>("--output", "-o")
        {
            Description = "Folder for virtual-panel PNG frames.",
        };
        var seconds = new Option<double?>("--seconds")
        {
            Description = "Stop after this many seconds. Omit to run until Ctrl+C.",
        };
        var fps = new Option<double?>("--fps")
        {
            Description = "Target frame rate. Omit to use the panel's own limit.",
        };
        var rotate = new Option<int?>("--rotate")
        {
            Description = "Rotate the image clockwise by 0, 90, 180, or 270 degrees. 0 is the panel's native orientation.",
        };
        var profile = new Option<string>("--profile")
        {
            Description = "Force a panel profile id, for example thermalright.trofeo-vision-6.86.",
        };
        var width = new Option<int?>("--width")
        {
            Description = "Virtual panel width. Only used with --virtual.",
        };
        var height = new Option<int?>("--height")
        {
            Description = "Virtual panel height. Only used with --virtual.",
        };

        var command = new Command("run", "Draw a theme on every attached panel until you stop it.")
        {
            theme,
            demo,
            includeVirtual,
            output,
            seconds,
            fps,
            rotate,
            profile,
            width,
            height,
        };
        command.SetAction((parse, cancellation) =>
            CliActions.RunAsync(
                new RunRequest(
                    parse.GetValue(verbose),
                    parse.GetValue(demo),
                    parse.GetValue(includeVirtual),
                    parse.GetValue(theme),
                    parse.GetValue(output),
                    parse.GetValue(seconds),
                    parse.GetValue(fps),
                    parse.GetValue(rotate),
                    parse.GetValue(profile),
                    parse.GetValue(width),
                    parse.GetValue(height)),
                cancellation));
        return command;
    }

    private static Command PreviewCommand(Option<bool> verbose)
    {
        var theme = new Option<string>("--theme", "-t")
        {
            Description = "Theme id or path. Defaults to \"rig\".",
            DefaultValueFactory = _ => "rig",
        };
        var output = new Option<string>("--output", "-o")
        {
            Description = "PNG file to write. Defaults to panelith-preview.png.",
            DefaultValueFactory = _ => "panelith-preview.png",
        };
        var width = new Option<int?>("--width")
        {
            Description = "Preview width. Defaults to the theme's canvas width.",
        };
        var height = new Option<int?>("--height")
        {
            Description = "Preview height. Defaults to the theme's canvas height.",
        };
        var demo = new Option<bool>("--demo")
        {
            Description = "Use simulated sensors. This is the default for preview.",
            DefaultValueFactory = _ => true,
        };
        var trofeo = new Option<bool>("--trofeo")
        {
            Description = "Also write 1280x480 and 1920x480 previews, the two Trofeo Vision sizes.",
        };

        var command = new Command("preview", "Render a theme to a PNG without a panel.")
        {
            theme,
            output,
            width,
            height,
            demo,
            trofeo,
        };
        command.SetAction((parse, cancellation) =>
            CliActions.PreviewAsync(
                parse.GetValue(verbose),
                parse.GetValue(demo),
                parse.GetValue(theme) ?? "rig",
                parse.GetValue(output) ?? "panelith-preview.png",
                parse.GetValue(width),
                parse.GetValue(height),
                parse.GetValue(trofeo),
                cancellation));
        return command;
    }

    private static Command DoctorCommand(Option<bool> verbose)
    {
        var install = new Option<bool>("--install-udev-rules")
        {
            Description = "Install Linux udev rules so your user can open the panel. Requires root.",
        };
        var print = new Option<bool>("--print-udev-rules")
        {
            Description = "Print the udev rules instead of installing them.",
        };

        var command = new Command("doctor", "Check that this machine can open a panel, and print the fix when it cannot.")
        {
            install,
            print,
        };
        command.SetAction((parse, cancellation) =>
            CliActions.DoctorAsync(
                parse.GetValue(verbose),
                parse.GetValue(install),
                parse.GetValue(print),
                cancellation));
        return command;
    }

    private static Command ProfilesCommand(Option<bool> verbose)
    {
        var command = new Command("profiles", "List every panel model Panelith knows how to drive.");
        command.SetAction((parse, cancellation) =>
            CliActions.ProfilesAsync(parse.GetValue(verbose), cancellation));
        return command;
    }

    private static Command SensorsCommand(Option<bool> verbose)
    {
        var demo = new Option<bool>("--demo")
        {
            Description = "List simulated sensors instead of this machine's.",
        };
        var filter = new Argument<string>("filter")
        {
            Description = "Only show sensors whose id or name contains this text.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("sensors", "List every sensor this machine reports and the aliases themes can bind to.")
        {
            demo,
            filter,
        };
        command.SetAction((parse, cancellation) =>
            CliActions.SensorsAsync(
                parse.GetValue(verbose),
                parse.GetValue(demo),
                parse.GetValue(filter),
                cancellation));
        return command;
    }

    private static Command ThemeCommand(Option<bool> verbose)
    {
        var theme = new Command("theme", "Inspect themes.");
        var list = new Command("list", "List themes Panelith can find.");
        list.SetAction((parse, cancellation) => CliActions.ThemeListAsync(parse.GetValue(verbose), cancellation));

        var id = new Argument<string>("theme")
        {
            Description = "Theme id or path to theme.json.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var validate = new Command("validate", "Check a theme for problems.")
        {
            id,
        };
        validate.SetAction((parse, cancellation) =>
            CliActions.ThemeValidateAsync(parse.GetValue(verbose), parse.GetValue(id), cancellation));

        theme.Subcommands.Add(list);
        theme.Subcommands.Add(validate);
        return theme;
    }

    /// <summary>Maps a clockwise angle onto the orientation the engine stores.</summary>
    internal static bool TryParseRotation(int degrees, out PanelOrientation orientation)
    {
        switch (degrees)
        {
            case 0:
                orientation = PanelOrientation.Portrait;
                return true;
            case 90:
                orientation = PanelOrientation.Landscape;
                return true;
            case 180:
                orientation = PanelOrientation.ReversePortrait;
                return true;
            case 270:
                orientation = PanelOrientation.ReverseLandscape;
                return true;
            default:
                orientation = PanelOrientation.Portrait;
                return false;
        }
    }

    /// <summary>Prints a rotation the way a person would say it.</summary>
    internal static string DescribeRotation(PanelOrientation orientation) => orientation switch
    {
        PanelOrientation.Landscape => "90° clockwise",
        PanelOrientation.ReversePortrait => "180°",
        PanelOrientation.ReverseLandscape => "270° clockwise",
        _ => "native",
    };

    internal static string FormatDouble(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}

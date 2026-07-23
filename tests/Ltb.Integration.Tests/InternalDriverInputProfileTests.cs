using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverInputProfileTests
{
    private const string ControllerType = "ltb_touch";
    private const string ReservedSystemClick = "/input/system/click";
    private const string VrChatAppKey = "steam.app.438100";

    [Fact]
    public void ProfileDeclaresNativeInputsAndLeftOnlyReservedSystemButton()
    {
        var repositoryRoot = FindRepositoryRoot();
        var inputRoot = Path.Combine(
            repositoryRoot,
            "native",
            "driver_ltb",
            "driver",
            "resources",
            "input");
        using var profileDocument = Parse(Path.Combine(inputRoot, "ltb_touch_profile.json"));
        var profile = profileDocument.RootElement;

        Assert.Equal("input_profile", profile.GetProperty("jsonid").GetString());
        Assert.Equal(ControllerType, profile.GetProperty("controller_type").GetString());
        Assert.Equal(
            "TrackedDeviceClass_Controller",
            profile.GetProperty("device_class").GetString());
        Assert.Equal("ltb", profile.GetProperty("resource_root").GetString());
        Assert.Equal("ltb", profile.GetProperty("driver_name").GetString());
        Assert.False(profile.TryGetProperty("compatibility_mode_controller_type", out _));

        var sources = profile.GetProperty("input_source");
        var rawPose = sources.GetProperty("/pose/raw");
        Assert.Equal("pose", rawPose.GetProperty("type").GetString());
        var system = sources.GetProperty("/input/system");
        Assert.Equal("button", system.GetProperty("type").GetString());
        Assert.Equal("left", system.GetProperty("side").GetString());
        Assert.True(system.GetProperty("click").GetBoolean());
        Assert.False(sources.TryGetProperty("/input/menu", out _));

        var nativeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "native",
            "driver_ltb",
            "src",
            "openvr",
            "controller_device.cpp"));
        var nativeComponents = Regex.Matches(
                nativeSource,
                """"(?<path>/input/[^"]+/(?:click|touch|value|x|y))"""")
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(nativeComponents);
        Assert.Contains(ReservedSystemClick, nativeComponents);
        foreach (var componentPath in nativeComponents)
        {
            var separator = componentPath.LastIndexOf('/');
            var sourcePath = componentPath[..separator];
            var component = componentPath[(separator + 1)..];
            Assert.True(
                sources.TryGetProperty(sourcePath, out var source),
                $"Native component '{componentPath}' has no input-profile source '{sourcePath}'.");

            if (component is "x" or "y")
            {
                Assert.Equal("joystick", source.GetProperty("type").GetString());
                continue;
            }

            Assert.True(
                source.TryGetProperty(component, out var declared) &&
                declared.ValueKind == JsonValueKind.True,
                $"Native component '{componentPath}' is not declared by the input profile.");
        }
    }

    [Fact]
    public void LeftMenuBitDrivesOnlyReservedSteamVrSystemButton()
    {
        var repositoryRoot = FindRepositoryRoot();
        var nativeRoot = Path.Combine(
            repositoryRoot,
            "native",
            "driver_ltb",
            "src",
            "openvr");
        var header = File.ReadAllText(Path.Combine(nativeRoot, "controller_device.hpp"));
        var source = File.ReadAllText(Path.Combine(nativeRoot, "controller_device.cpp"));

        Assert.Contains("SystemClick", header, StringComparison.Ordinal);
        Assert.DoesNotContain("MenuClick", header, StringComparison.Ordinal);
        Assert.DoesNotContain("/input/menu/click", source, StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(source, Regex.Escape(ReservedSystemClick)).Cast<Match>());
        Assert.Matches(
            new Regex(
                """
                \(!left\s*\|\|\s*
                CreateBoolean\(\s*
                    property_container_,\s*
                    Input::SystemClick,\s*
                    "/input/system/click"\s*
                \)\)
                """,
                RegexOptions.CultureInvariant |
                RegexOptions.IgnorePatternWhitespace |
                RegexOptions.Singleline),
            source);
        Assert.Matches(
            new Regex(
                """
                if\s*\(\s*hand_\s*==\s*Hand::Left\s*\)\s*\{\s*
                    vr::VRDriverInput\(\)->UpdateBooleanComponent\(\s*
                        inputs_\[InputIndex\(Input::SystemClick\)\],\s*
                        HasButton\(\s*
                            published_input\.buttons,\s*
                            ButtonBit::Menu\s*
                        \),\s*
                        input_time_offset\s*
                    \);\s*
                \}
                """,
                RegexOptions.CultureInvariant |
                RegexOptions.IgnorePatternWhitespace |
                RegexOptions.Singleline),
            source);
    }

    [Fact]
    public void ProfileResourcesAndCompatibilityBindingsAreParseableAndSelfContained()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resourcesRoot = Path.Combine(
            repositoryRoot,
            "native",
            "driver_ltb",
            "driver",
            "resources");
        var inputRoot = Path.Combine(resourcesRoot, "input");
        using var profileDocument = Parse(Path.Combine(inputRoot, "ltb_touch_profile.json"));
        var profile = profileDocument.RootElement;
        var sources = profile.GetProperty("input_source");

        var remappingPath = ResolveDriverResource(
            resourcesRoot,
            profile.GetProperty("remapping").GetString());
        var legacyBindingPath = ResolveDriverResource(
            resourcesRoot,
            profile.GetProperty("legacy_binding").GetString());
        using var remappingDocument = Parse(remappingPath);
        using var legacyBindingDocument = Parse(legacyBindingPath);
        using var localizationDocument = Parse(Path.Combine(
            resourcesRoot,
            "localization",
            "localization.json"));
        var localization = Assert.Single(localizationDocument.RootElement.EnumerateArray());
        Assert.Equal("System", localization.GetProperty("/input/system").GetString());

        var defaultBindings = profile.GetProperty("default_bindings");
        var vrChatBinding = Assert.Single(defaultBindings.EnumerateArray());
        Assert.Equal(VrChatAppKey, vrChatBinding.GetProperty("app_key").GetString());
        var vrChatBindingPath = Path.Combine(
            inputRoot,
            vrChatBinding.GetProperty("binding_url").GetString()!
                .Replace('/', Path.DirectorySeparatorChar));
        using var vrChatDocument = Parse(vrChatBindingPath);

        foreach (var imageProperty in new[] { "input_bindingui_left", "input_bindingui_right" })
        {
            var imagePath = ResolveDriverResource(
                resourcesRoot,
                profile.GetProperty(imageProperty).GetProperty("image").GetString());
            Assert.True(File.Exists(imagePath), $"Missing binding UI image '{imagePath}'.");
        }

        AssertBindingOnlyUsesDeclaredSources(legacyBindingDocument.RootElement, sources);
        AssertBindingOnlyUsesDeclaredSources(vrChatDocument.RootElement, sources);
        var legacyJson = File.ReadAllText(legacyBindingPath);
        Assert.Contains("/actions/legacy/in/left_grip_press", legacyJson, StringComparison.Ordinal);
        Assert.Contains("/actions/legacy/in/right_grip_press", legacyJson, StringComparison.Ordinal);
    }

    [Fact]
    public void RemappingAndVrChatDefaultBindingProvideTouchCompatibilityWithoutUnsupportedFeatures()
    {
        var repositoryRoot = FindRepositoryRoot();
        var inputRoot = Path.Combine(
            repositoryRoot,
            "native",
            "driver_ltb",
            "driver",
            "resources",
            "input");
        using var remappingDocument = Parse(Path.Combine(inputRoot, "ltb_touch_remapping.json"));
        var remapping = remappingDocument.RootElement;

        Assert.Equal(ControllerType, remapping.GetProperty("to_controller_type").GetString());
        var layout = Assert.Single(remapping.GetProperty("layouts").EnumerateArray());
        Assert.Equal("oculus_touch", layout.GetProperty("from_controller_type").GetString());
        Assert.True(layout.GetProperty("simulate_controller_type").GetBoolean());
        Assert.True(layout.GetProperty("simulate_render_model").GetBoolean());
        Assert.False(layout.GetProperty("simulate_HMD").GetBoolean());

        var automaticMappings = layout.GetProperty("autoremappings")
            .EnumerateArray()
            .Select(item => (
                From: item.GetProperty("from").GetString(),
                To: item.GetProperty("to").GetString()))
            .ToArray();
        Assert.Contains(
            ("/user/hand/right/input/joystick", "/user/hand/right/input/thumbstick"),
            automaticMappings);
        Assert.DoesNotContain(
            automaticMappings,
            mapping =>
                mapping.From?.Contains("/input/system", StringComparison.Ordinal) == true ||
                mapping.To?.Contains("/input/system", StringComparison.Ordinal) == true ||
                mapping.From?.Contains("/input/menu", StringComparison.Ordinal) == true ||
                mapping.To?.Contains("/input/menu", StringComparison.Ordinal) == true);

        using var vrChatDocument = Parse(Path.Combine(
            inputRoot,
            "bindings",
            "bindings_ltb_touch_vrchat.json"));
        var vrChat = vrChatDocument.RootElement;
        Assert.Equal(VrChatAppKey, vrChat.GetProperty("app_key").GetString());
        Assert.Equal(ControllerType, vrChat.GetProperty("controller_type").GetString());
        var options = vrChat.GetProperty("options");
        Assert.Equal("oculus_touch", options.GetProperty("simulated_controller_type").GetString());
        Assert.True(options.GetProperty("simulate_rendermodel").GetBoolean());
        Assert.False(options.GetProperty("simulate_hmd").GetBoolean());

        var userPaths = UserPaths(vrChat).ToArray();
        Assert.Contains("/user/hand/left/pose/raw", userPaths);
        Assert.Contains("/user/hand/right/pose/raw", userPaths);
        Assert.Contains("/user/hand/left/input/thumbstick", userPaths);
        Assert.Contains("/user/hand/right/input/thumbstick", userPaths);
        Assert.DoesNotContain(userPaths, path => path.Contains("/input/joystick", StringComparison.Ordinal));
        Assert.DoesNotContain(userPaths, path => path.Contains("/input/menu", StringComparison.Ordinal));
        Assert.DoesNotContain(userPaths, path => path.Contains("/input/system", StringComparison.Ordinal));
        Assert.DoesNotContain(userPaths, path => path.Contains("/input/skeleton", StringComparison.Ordinal));
        Assert.DoesNotContain(userPaths, path => path.Contains("/output/haptic", StringComparison.Ordinal));

        var vrChatAppMenuPaths = vrChat
            .GetProperty("bindings")
            .GetProperty("/actions/global")
            .GetProperty("sources")
            .EnumerateArray()
            .Where(source =>
                source.GetProperty("inputs").TryGetProperty("click", out var click) &&
                string.Equals(
                    click.GetProperty("output").GetString(),
                    "/actions/global/in/menu",
                    StringComparison.Ordinal))
            .Select(source => source.GetProperty("path").GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "/user/hand/left/input/y",
                "/user/hand/right/input/b",
            ],
            vrChatAppMenuPaths);

        var bindingJson = File.ReadAllText(Path.Combine(
            inputRoot,
            "bindings",
            "bindings_ltb_touch_vrchat.json"));
        Assert.Contains("/actions/global/in/menu", bindingJson, StringComparison.Ordinal);
        Assert.Contains("/actions/global/in/trigger_axis", bindingJson, StringComparison.Ordinal);
        Assert.Contains("/actions/global/in/grip_axis", bindingJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageAndWindowsArtifactContractsIncludeEveryReferencedInputResource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageScript = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "build",
            "package-win-x64.sh"));
        var workflow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "build-internal-drivers.yml"));
        var requiredResources = new[]
        {
            "resources/input/ltb_touch_profile.json",
            "resources/input/ltb_touch_remapping.json",
            "resources/input/legacy_bindings_ltb_touch.json",
            "resources/input/bindings/bindings_ltb_touch_vrchat.json",
        };

        foreach (var resource in requiredResources)
        {
            Assert.Contains(resource, packageScript, StringComparison.Ordinal);
            Assert.Contains(resource, workflow, StringComparison.Ordinal);
        }
    }

    private static void AssertBindingOnlyUsesDeclaredSources(
        JsonElement binding,
        JsonElement declaredSources)
    {
        foreach (var userPath in UserPaths(binding))
        {
            Assert.DoesNotContain("/output/", userPath, StringComparison.Ordinal);
            Assert.DoesNotContain("/input/skeleton", userPath, StringComparison.Ordinal);

            var sourcePath = ToProfileSourcePath(userPath);
            Assert.True(
                declaredSources.TryGetProperty(sourcePath, out _),
                $"Binding path '{userPath}' targets undeclared source '{sourcePath}'.");
        }

        var actionSets = binding.GetProperty("bindings");
        foreach (var actionSet in actionSets.EnumerateObject())
        {
            if (actionSet.Value.TryGetProperty("sources", out var bindingSources))
            {
                foreach (var bindingSource in bindingSources.EnumerateArray())
                {
                    AssertSourceBindingUsesDeclaredCapabilities(bindingSource, declaredSources);
                }
            }

            if (actionSet.Value.TryGetProperty("chords", out var chords))
            {
                foreach (var chord in chords.EnumerateArray())
                {
                    foreach (var chordInput in chord.GetProperty("inputs").EnumerateArray())
                    {
                        var parts = chordInput.EnumerateArray().ToArray();
                        Assert.Equal(2, parts.Length);
                        var sourcePath = ToProfileSourcePath(parts[0].GetString()!);
                        var source = declaredSources.GetProperty(sourcePath);
                        Assert.True(
                            Declares(source, "click") || Declares(source, "value"),
                            $"Chord input '{parts[0].GetString()}' requires a click-capable source.");
                    }
                }
            }
        }
    }

    private static void AssertSourceBindingUsesDeclaredCapabilities(
        JsonElement bindingSource,
        JsonElement declaredSources)
    {
        var userPath = bindingSource.GetProperty("path").GetString()!;
        var sourcePath = ToProfileSourcePath(userPath);
        var declared = declaredSources.GetProperty(sourcePath);
        var sourceType = declared.GetProperty("type").GetString();

        foreach (var input in bindingSource.GetProperty("inputs").EnumerateObject())
        {
            var supported = input.Name switch
            {
                "click" => Declares(declared, "click") || Declares(declared, "value"),
                "touch" => Declares(declared, "touch"),
                "pull" => Declares(declared, "value"),
                "position" => sourceType == "joystick",
                _ => false,
            };
            Assert.True(
                supported,
                $"Binding '{userPath}' uses undeclared '{input.Name}' capability.");
        }
    }

    private static bool Declares(JsonElement source, string capability) =>
        source.TryGetProperty(capability, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> UserPaths(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var path in UserPaths(property.Value))
                    {
                        yield return path;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var path in UserPaths(item))
                    {
                        yield return path;
                    }
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null &&
                    value.StartsWith("/user/hand/", StringComparison.Ordinal))
                {
                    yield return value;
                }
                break;
        }
    }

    private static string ToProfileSourcePath(string userPath)
    {
        var segments = userPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(
            segments.Length == 5 &&
            segments[0] == "user" &&
            segments[1] == "hand" &&
            segments[2] is "left" or "right" &&
            segments[3] is "input" or "pose",
            $"Unexpected binding path '{userPath}'.");
        return $"/{segments[3]}/{segments[4]}";
    }

    private static JsonDocument Parse(string path)
    {
        Assert.True(File.Exists(path), $"Missing SteamVR input resource '{path}'.");
        return JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
    }

    private static string ResolveDriverResource(string resourcesRoot, string? resourcePath)
    {
        Assert.NotNull(resourcePath);
        const string prefix = "{ltb}/";
        Assert.StartsWith(prefix, resourcePath, StringComparison.Ordinal);
        var relative = resourcePath[prefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(resourcesRoot, relative);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LighthouseTouchBridge.sln")))
            {
                return directory.FullName;
            }
        }

        throw new FileNotFoundException("Could not locate LighthouseTouchBridge.sln.");
    }
}

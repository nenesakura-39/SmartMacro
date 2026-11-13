using System.Diagnostics;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

public class AssetAndCloneTests
{
    private static string MakeLargePngBase64(int seed)
    {
        var img = TestImages.Icon(48, 48, seed);
        return Convert.ToBase64String(PngCodec.Encode(img));
    }

    private static string MakeLargeTemplateData(int seed)
    {
        var img = TestImages.Icon(48, 48, seed);
        return TemplateCodec.Encode(img);
    }

    private static Macro BuildRichMacroWithAllComplexSteps(string sharedTemplate, string sharedThumb)
    {
        var nestedFind = new FindImageStep
        {
            Comment = "Nested find",
            TemplateWidth = 48,
            TemplateHeight = 48,
            TemplateData = sharedTemplate,
            AdditionalTemplates = new List<TemplateVariant>
            {
                new() { Width = 48, Height = 48, Data = sharedTemplate },
            },
            FailurePolicy = new StepFailurePolicy { Action = StepFailureAction.Retry, RetryCount = 2, RetryDelayMs = 150 },
        };

        var secondaryFind = new FindImageStep
        {
            Comment = "Secondary find",
            TemplateWidth = 48,
            TemplateHeight = 48,
            TemplateData = sharedTemplate,
        };

        var compoundCondition = new Condition
        {
            Check = nestedFind,
            SecondCombinator = ConditionCombinator.And,
            SecondCondition = new Condition { Check = secondaryFind, Negate = true },
        };

        return new Macro
        {
            Name = "Rich Macro",
            Description = "Covers every mutable structure",
            ProfileName = "Default",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "count", Type = MacroVariableType.Number, DefaultValue = "1" },
            },
            Watchers = new List<MacroWatcher>
            {
                new()
                {
                    Name = "Popup guard",
                    Condition = new Condition { Check = secondaryFind.Clone() },
                    HandlerMacroId = Guid.NewGuid(),
                },
            },
            Triggers = new List<MacroTrigger>
            {
                new() { Kind = TriggerKind.Schedule, IntervalMinutes = 5 },
            },
            Steps = new List<MacroStep>
            {
                new DelayStep { DurationMs = 250 },
                new MouseMoveStep { X = 120, Y = 240, UseRelativeDelta = true },
                new MouseClickStep
                {
                    X = 100,
                    Y = 200,
                    TargetThumbnailBase64 = sharedThumb,
                    Target = new TargetFingerprint
                    {
                        WindowTitle = "Editor",
                        WindowProcessName = "app.exe",
                        NearbyText = "Save",
                    },
                },
                new MouseScrollStep { Delta = -240 },
                new KeyPressStep { VirtualKey = 0x0D, HoldMs = 40 },
                new TypeTextStep { Text = "hello" },
                new FindTextStep
                {
                    Text = "OK",
                    FailurePolicy = new StepFailurePolicy { Action = StepFailureAction.StopMacro },
                },
                (FindImageStep)nestedFind.Clone(),
                new FindColorStep
                {
                    Color = "#0A141E",
                    FailurePolicy = new StepFailurePolicy { Action = StepFailureAction.Continue },
                },
                new InputSequenceStep
                {
                    Events = new List<InputEvent>
                    {
                        InputEvent.ButtonDown(100, MacroMouseButton.Left, 10, 20),
                        InputEvent.ButtonUp(500, MacroMouseButton.Left, 10, 20),
                    },
                    MouseSegments = new List<MouseSegment> { new() { StartUs = 0, Detected = MouseMovementMode.CursorPosition } },
                    ClickTargets = new List<SequenceClickTarget>
                    {
                        new()
                        {
                            EventIndex = 0,
                            TargetThumbnailBase64 = sharedThumb,
                            Target = new TargetFingerprint { WindowTitle = "Game", WindowProcessName = "game.exe" },
                        },
                    },
                },
                new CheckpointStep { Name = "Start" },
                new CallMacroStep
                {
                    TargetMacroId = Guid.NewGuid(),
                    Inputs = new List<VariableBinding> { new() { TargetVariable = "arg", SourceExpression = "1" } },
                    Outputs = new List<VariableBinding> { new() { TargetVariable = "outVar", SourceExpression = "res" } },
                },
                new WaitForChangeStep
                {
                    FailurePolicy = new StepFailurePolicy { Action = StepFailureAction.Retry, RetryCount = 3 },
                },
                new FocusWindowStep
                {
                    WindowTitle = "App",
                    SnapLayout = WindowSnapLayout.SnapRightHalf,
                    FailurePolicy = new StepFailurePolicy { Action = StepFailureAction.Continue },
                },
                new ScreenshotStep { Comment = "snap", SoundCue = SoundCueKind.SuccessChime },
                new SetVariableStep { VariableName = "count", Source = VariableSourceKind.Constant, Value = "2" },
                new IfStep { Condition = compoundCondition.Clone() },
                new ElseStep(),
                new EndIfStep(),
                new RepeatStep { Mode = RepeatMode.Times, Count = 2, Condition = compoundCondition.Clone() },
                new EndRepeatStep(),
                new FlowControlStep { Action = FlowControlAction.ContinueLoop },
                new FeedbackLoopStep
                {
                    CompleteWhen = compoundCondition,
                    FailWhen = new Condition
                    {
                        Check = new FindImageStep
                        {
                            Comment = "FailCheck",
                            TemplateWidth = 48,
                            TemplateHeight = 48,
                            TemplateData = sharedTemplate,
                        },
                    },
                },
            },
        };
    }

    [Fact]
    public void DeepClone_covers_every_step_subtype_preserves_json_parity_isolates_mutables_and_shares_strings()
    {
        var sharedTemplate = MakeLargeTemplateData(11);
        var sharedThumb = MakeLargePngBase64(22);
        var original = BuildRichMacroWithAllComplexSteps(sharedTemplate, sharedThumb);

        // Verify test macro genuinely covers every non-abstract MacroStep subclass in SmartMacro.Core.
        var allStepTypes = typeof(MacroStep).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(MacroStep)))
            .ToHashSet();
        var coveredTypes = original.Steps.Select(s => s.GetType()).ToHashSet();
        Assert.Subset(allStepTypes, coveredTypes);
        Assert.Equal(allStepTypes.Count, coveredTypes.Count);

        var clone = original.DeepClone();

        // Exact JSON parity prior to mutation.
        Assert.Equal(MacroJson.Serialize(original), MacroJson.Serialize(clone));

        // Immutable image payloads keep sharing the exact same string reference in RAM.
        var origFind = original.Steps.OfType<FindImageStep>().First();
        var cloneFind = clone.Steps.OfType<FindImageStep>().First();
        Assert.Same(origFind.TemplateData, cloneFind.TemplateData);
        Assert.Same(origFind.AdditionalTemplates![0].Data, cloneFind.AdditionalTemplates![0].Data);

        var origClick = original.Steps.OfType<MouseClickStep>().First();
        var cloneClick = clone.Steps.OfType<MouseClickStep>().First();
        Assert.Same(origClick.TargetThumbnailBase64, cloneClick.TargetThumbnailBase64);

        var origSeq = original.Steps.OfType<InputSequenceStep>().First();
        var cloneSeq = clone.Steps.OfType<InputSequenceStep>().First();
        Assert.Same(origSeq.ClickTargets![0].TargetThumbnailBase64, cloneSeq.ClickTargets![0].TargetThumbnailBase64);

        // Mutating every mutable list / nested object in clone leaves original untouched.
        clone.Name = "Mutated";
        clone.Steps.Clear();
        clone.Variables![0].DefaultValue = "999";
        ((FindImageStep)clone.Watchers![0].Condition.Check!).TemplateWidth = 999;
        clone.Triggers![0].IntervalMinutes = 99;
        cloneClick.Target!.WindowTitle = "ChangedTitle";
        cloneFind.FailurePolicy!.RetryCount = 99;
        cloneFind.AdditionalTemplates![0].Width = 999;
        cloneSeq.Events.Add(InputEvent.KeyDown(999, 65));
        cloneSeq.MouseSegments![0].StartUs = 12345;
        cloneSeq.ClickTargets![0].Target!.WindowTitle = "OtherGame";

        Assert.Equal("Rich Macro", original.Name);
        Assert.Equal(allStepTypes.Count, original.Steps.Count);
        Assert.Equal("1", original.Variables![0].DefaultValue);
        Assert.Equal(48, ((FindImageStep)original.Watchers![0].Condition.Check!).TemplateWidth);
        Assert.Equal(5, original.Triggers![0].IntervalMinutes);
        Assert.Equal("Editor", origClick.Target!.WindowTitle);
        Assert.Equal(2, origFind.FailurePolicy!.RetryCount);
        Assert.Equal(48, origFind.AdditionalTemplates![0].Width);
        Assert.Equal(2, origSeq.Events.Count);
        Assert.Equal(0, origSeq.MouseSegments![0].StartUs);
        Assert.Equal("Game", origSeq.ClickTargets![0].Target!.WindowTitle);

        // Also mutate nested CallMacro bindings and workflow conditions on a second clone.
        var clone2 = original.DeepClone();
        var cloneCall = clone2.Steps.OfType<CallMacroStep>().First();
        cloneCall.Inputs![0].SourceExpression = "changed";
        cloneCall.Outputs!.Add(new VariableBinding { TargetVariable = "newVar", SourceExpression = "extra" });
        var cloneIf = clone2.Steps.OfType<IfStep>().First();
        ((FindImageStep)cloneIf.Condition!.SecondCondition!.Check!).Comment = "MutatedSecond";
        var cloneFeedback = clone2.Steps.OfType<FeedbackLoopStep>().First();
        ((FindImageStep)cloneFeedback.FailWhen!.Check!).Comment = "MutatedFail";

        var origCall = original.Steps.OfType<CallMacroStep>().First();
        Assert.Equal("1", origCall.Inputs![0].SourceExpression);
        Assert.Single(origCall.Outputs!);
        Assert.Equal("Secondary find", ((FindImageStep)original.Steps.OfType<IfStep>().First().Condition!.SecondCondition!.Check!).Comment);
        Assert.Equal("FailCheck", ((FindImageStep)original.Steps.OfType<FeedbackLoopStep>().First().FailWhen!.Check!).Comment);
    }

    [Fact]
    public void Structural_MacroJson_Clone_is_dramatically_faster_than_json_roundtrip()
    {
        var templateA = MakeLargeTemplateData(31);
        var templateB = MakeLargeTemplateData(32);
        var macro = new Macro { Name = "Large" };
        for (var i = 0; i < 120; i++)
        {
            macro.Steps.Add(new FindImageStep
            {
                TemplateWidth = 48,
                TemplateHeight = 48,
                TemplateData = (i % 2 == 0) ? templateA : templateB,
            });
        }

        // Warm up JIT & serializer metadata cleanly.
        _ = MacroJson.Clone(macro);
        _ = MacroJson.Deserialize<Macro>(MacroJson.Serialize(macro));

        var swClone = Stopwatch.StartNew();
        for (var i = 0; i < 40; i++)
        {
            _ = MacroJson.Clone(macro);
        }
        swClone.Stop();

        var swJson = Stopwatch.StartNew();
        for (var i = 0; i < 40; i++)
        {
            _ = MacroJson.Deserialize<Macro>(MacroJson.Serialize(macro));
        }
        swJson.Stop();

        Assert.True(
            swClone.ElapsedTicks * 10L < swJson.ElapsedTicks,
            $"Expected structural MacroJson.Clone ({swClone.Elapsed.TotalMilliseconds:F2}ms) to be >10x faster than JSON round-trip ({swJson.Elapsed.TotalMilliseconds:F2}ms).");
    }

    [Fact]
    public void MacroAssetStore_deduplicates_repeated_images_shrinks_file_and_hydrates_interned_strings_on_load()
    {
        var repeatedTemplate = MakeLargeTemplateData(51);
        var repeatedThumb = MakeLargePngBase64(52);
        var uniqueTemplate = MakeLargeTemplateData(53);

        var macro = new Macro
        {
            Name = "Deduplicated",
            Steps =
            {
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = repeatedTemplate },
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(repeatedTemplate.AsSpan()) },
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(repeatedTemplate.AsSpan()) },
                new MouseClickStep { X = 10, Y = 10, TargetThumbnailBase64 = repeatedThumb },
                new MouseClickStep { X = 20, Y = 20, TargetThumbnailBase64 = new string(repeatedThumb.AsSpan()) },
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = uniqueTemplate },
                new IfStep
                {
                    Condition = new Condition
                    {
                        Check = new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(repeatedTemplate.AsSpan()) },
                    },
                },
            },
        };

        var json = MacroFileFormat.Serialize(macro);
        var parsedDoc = MacroJson.Deserialize<MacroDocument>(json)!;

        // Only repeated payloads are moved to Assets; unique stays inline.
        Assert.NotNull(parsedDoc.Macro!.Assets);
        var expectedTemplateId = MacroAssetStore.ComputeAssetId(repeatedTemplate);
        var expectedThumbId = MacroAssetStore.ComputeAssetId(repeatedThumb);
        Assert.Equal(2, parsedDoc.Macro.Assets!.Count);
        Assert.Equal(repeatedTemplate, parsedDoc.Macro.Assets[expectedTemplateId]);
        Assert.Equal(repeatedThumb, parsedDoc.Macro.Assets[expectedThumbId]);

        var dehydratedStep0 = (FindImageStep)parsedDoc.Macro.Steps[0];
        Assert.Equal(string.Empty, dehydratedStep0.TemplateData);
        Assert.Equal(expectedTemplateId, dehydratedStep0.TemplateAssetId);

        var dehydratedUnique = (FindImageStep)parsedDoc.Macro.Steps[5];
        Assert.Equal(uniqueTemplate, dehydratedUnique.TemplateData);
        Assert.Null(dehydratedUnique.TemplateAssetId);

        // Original in-memory macro passed to Serialize was never mutated.
        Assert.Equal(repeatedTemplate, ((FindImageStep)macro.Steps[0]).TemplateData);
        Assert.Null(macro.Assets);

        // Parsing through MacroFileFormat rehydrates inline strings, releases Assets, and interns duplicates.
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.Assets);

        var f0 = (FindImageStep)loaded.Steps[0];
        var f1 = (FindImageStep)loaded.Steps[1];
        var f2 = (FindImageStep)loaded.Steps[2];
        var fIf = (FindImageStep)((IfStep)loaded.Steps[6]).Condition!.Check!;
        var c0 = (MouseClickStep)loaded.Steps[3];
        var c1 = (MouseClickStep)loaded.Steps[4];

        Assert.Equal(repeatedTemplate, f0.TemplateData);
        Assert.Same(f0.TemplateData, f1.TemplateData);
        Assert.Same(f0.TemplateData, f2.TemplateData);
        Assert.Same(f0.TemplateData, fIf.TemplateData);
        Assert.Null(f0.TemplateAssetId);

        Assert.Equal(repeatedThumb, c0.TargetThumbnailBase64);
        Assert.Same(c0.TargetThumbnailBase64, c1.TargetThumbnailBase64);
        Assert.Null(c0.SnapshotAssetId);
    }

    [Fact]
    public void Single_image_macro_keeps_inline_storage_and_legacy_inline_loads_keep_working()
    {
        var template = MakeLargeTemplateData(61);
        var singleMacro = new Macro
        {
            Name = "LegacyCompat",
            Steps =
            {
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = template },
            },
        };

        var json = MacroFileFormat.Serialize(singleMacro);
        Assert.DoesNotContain("\"assets\"", json, StringComparison.OrdinalIgnoreCase);

        var doc = MacroJson.Deserialize<MacroDocument>(json)!;
        Assert.Equal(4, doc.SchemaVersion);
        Assert.Null(doc.Macro!.Assets);
        Assert.Equal(template, ((FindImageStep)doc.Macro.Steps[0]).TemplateData);

        // Simulate legacy v4 file with duplicate inline blobs and zero asset fields.
        var legacyDuplicateJson = $$"""
        {
          "schemaVersion": 4,
          "macro": {
            "id": "{{Guid.NewGuid()}}",
            "name": "LegacyDup",
            "steps": [
              { "type": "findImage", "templateWidth": 48, "templateHeight": 48, "templateData": "{{template}}" },
              { "type": "findImage", "templateWidth": 48, "templateHeight": 48, "templateData": "{{template}}" }
            ]
          }
        }
        """;

        Assert.True(MacroFileFormat.TryParse(legacyDuplicateJson, out var parsedLegacy, out var error), error);
        var s0 = (FindImageStep)parsedLegacy!.Steps[0];
        var s1 = (FindImageStep)parsedLegacy.Steps[1];
        Assert.Equal(template, s0.TemplateData);
        Assert.Same(s0.TemplateData, s1.TemplateData);
    }

    [Fact]
    public void Bundled_submacros_deduplicate_on_save_and_share_interned_strings_across_bundle_on_load()
    {
        var sharedImg = MakeLargeTemplateData(66);
        var subId = Guid.NewGuid();
        var sub = new Macro
        {
            Id = subId,
            Name = "Sub",
            Steps =
            {
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(sharedImg.AsSpan()) },
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(sharedImg.AsSpan()) },
            },
        };
        var main = new Macro
        {
            Name = "Main",
            Steps =
            {
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(sharedImg.AsSpan()) },
                new FindImageStep { TemplateWidth = 48, TemplateHeight = 48, TemplateData = new string(sharedImg.AsSpan()) },
                new CallMacroStep { TargetMacroId = subId, TargetMacroName = "Sub" },
            },
        };

        var json = MacroFileFormat.Serialize(main, new[] { main, sub });
        Assert.True(MacroFileFormat.TryParseBundle(json, out var loadedMain, out var loadedSubs, out var error), error);

        Assert.Single(loadedSubs);
        var mainFind0 = (FindImageStep)loadedMain!.Steps[0];
        var mainFind1 = (FindImageStep)loadedMain.Steps[1];
        var subFind0 = (FindImageStep)loadedSubs[0].Steps[0];
        var subFind1 = (FindImageStep)loadedSubs[0].Steps[1];

        Assert.Equal(sharedImg, mainFind0.TemplateData);
        Assert.Same(mainFind0.TemplateData, mainFind1.TemplateData);
        Assert.Same(mainFind0.TemplateData, subFind0.TemplateData);
        Assert.Same(mainFind0.TemplateData, subFind1.TemplateData);
    }

    [Fact]
    public void StepEditHistory_captures_minimal_deltas_supports_in_place_edits_and_enforces_byte_budget()
    {
        var sharedImage = MakeLargeTemplateData(71);
        var steps = new List<MacroStep>();
        for (var i = 0; i < 40; i++)
        {
            steps.Add(new FindImageStep
            {
                Comment = $"Step {i}",
                TemplateWidth = 48,
                TemplateHeight = 48,
                TemplateData = sharedImage,
            });
        }

        var history = new StepEditHistory();

        // Edit #1: replace middle step #20 via BeginEdit / CommitPending.
        history.BeginEdit(steps);
        var originalMid = (FindImageStep)steps[20];
        steps[20] = new DelayStep { DurationMs = 500 };
        Assert.True(history.CommitPending(steps));
        Assert.True(history.CanUndo);
        Assert.Equal(1, history.UndoCount);

        // Delta byte footprint must reflect ~1 step, NOT all 40 steps.
        Assert.True(history.RetainedBytes < 10_000, $"Expected compact delta bytes (< 10KB), got {history.RetainedBytes}.");

        // Edit #2: in-place mutation (models ApplySuggestion / ToggleEnabled pattern).
        history.BeginEdit(steps);
        ((FindImageStep)steps[5]).MinSimilarityPercent = 72;
        Assert.True(history.CommitPending(steps));
        Assert.Equal(2, history.UndoCount);

        // Undo edit #2: step #5 similarity reverts cleanly and prefix/suffix still share sharedImage ref.
        Assert.True(history.TryUndo(steps, out var afterUndo2));
        Assert.Equal(90, ((FindImageStep)afterUndo2[5]).MinSimilarityPercent);
        Assert.Same(sharedImage, ((FindImageStep)afterUndo2[0]).TemplateData);

        // Undo edit #1: original step #20 returns with same Id and sharedImage.
        Assert.True(history.TryUndo(afterUndo2, out var afterUndo1));
        Assert.IsType<FindImageStep>(afterUndo1[20]);
        Assert.Equal(originalMid.Id, afterUndo1[20].Id);
        Assert.Same(sharedImage, ((FindImageStep)afterUndo1[20]).TemplateData);

        // Redo both edits cleanly.
        Assert.True(history.TryRedo(afterUndo1, out var afterRedo1));
        Assert.IsType<DelayStep>(afterRedo1[20]);
        Assert.True(history.TryRedo(afterRedo1, out var afterRedo2));
        Assert.Equal(72, ((FindImageStep)afterRedo2[5]).MinSimilarityPercent);

        // Memory-bounded eviction check.
        var bounded = new StepEditHistory(capacity: 50, maxBytes: 4_500);
        var list = new List<MacroStep>();
        for (var i = 0; i < 12; i++)
        {
            bounded.BeginEdit(list);
            list.Add(new TypeTextStep { Text = new string((char)('a' + i), 600) }); // ~1224 B each
            bounded.CommitPending(list);
        }

        Assert.True(bounded.RetainedBytes <= bounded.MaxBytes, $"RetainedBytes ({bounded.RetainedBytes}) exceeded MaxBytes ({bounded.MaxBytes}).");
        Assert.True(bounded.UndoCount < 12);
        Assert.True(bounded.UndoCount >= 1);
    }

    [Fact]
    public void UndoHistory_enforces_retained_byte_budget()
    {
        var history = new UndoHistory(capacity: 50, maxBytes: 2_500);
        for (var i = 0; i < 10; i++)
        {
            // 600 chars = 1,200 UTF-16 bytes each.
            history.Record(new string((char)('A' + i), 600));
        }

        Assert.True(history.RetainedBytes <= history.MaxBytes);
        Assert.True(history.UndoCount <= 2);

        // A single giant snapshot still stays retained so the most recent undo is never dropped.
        var giant = new UndoHistory(capacity: 10, maxBytes: 500);
        giant.Record(new string('Z', 1_000));
        Assert.Equal(1, giant.UndoCount);
        Assert.True(giant.TryUndo("cur", out var popped));
        Assert.Equal(1_000, popped.Length);
    }

    [Fact]
    public void TemplateCache_digest_keys_hit_efficiently_and_never_collide_on_stale_asset_id()
    {
        var cache = new TemplateCache();
        var png1 = MakeLargePngBase64(81);
        var png2 = MakeLargePngBase64(82);

        var tpl1 = MakeLargeTemplateData(83);
        var tpl2 = MakeLargeTemplateData(84);

        // Same assetId with identical content copy hits cache (only 1 decode).
        Assert.True(cache.TryGetPng(png1, "sha256-sameid", out var decodedPng1, out _));
        Assert.True(cache.TryGetPng(new string(png1.AsSpan()), "sha256-sameid", out var cachedPng1, out _));
        Assert.Same(decodedPng1, cachedPng1);

        // Different PNG bytes claiming the SAME assetId must not mis-hit!
        Assert.True(cache.TryGetPng(png2, "sha256-sameid", out var decodedPng2, out _));
        Assert.NotSame(decodedPng1, decodedPng2);

        // Same check for compressed templates.
        Assert.True(cache.TryGetTemplate(tpl1, "sha256-tplid", 48, 48, out var decodedTpl1, out _));
        Assert.True(cache.TryGetTemplate(new string(tpl1.AsSpan()), "sha256-tplid", 48, 48, out var cachedTpl1, out _));
        Assert.Same(decodedTpl1, cachedTpl1);

        Assert.True(cache.TryGetTemplate(tpl2, "sha256-tplid", 48, 48, out var decodedTpl2, out _));
        Assert.NotSame(decodedTpl1, decodedTpl2);

        Assert.Equal(4, cache.Decodes);
        Assert.Equal(2, cache.Hits);
    }
}

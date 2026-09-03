using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.Core.Tests;

[TestClass]
public sealed class JsonConfigurationStoreTests
{
    [TestMethod]
    public void DocumentParser_MigratesVersionOnePreferences()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "preferences": { "runAtStartup": true },
              "trigger": { "kind": "mouseButton", "button": "xButton2", "activationBehavior": "hold" }
            }
            """;

        var parsed = ConfigurationDocumentParser.Parse(json);

        Assert.IsTrue(parsed.WasMigrated);
        Assert.AreEqual(ConfigurationSchema.CurrentVersion, parsed.Configuration.SchemaVersion);
        Assert.IsTrue(parsed.Configuration.Preferences.General.RunAtStartup);
        Assert.AreEqual(ActivationMode.Hold, parsed.Configuration.Trigger.ActivationMode);
        Assert.AreEqual(1, parsed.Configuration.UserProfiles.Count);
    }

    [TestMethod]
    public void DocumentParser_MigratesVersionTwoIntoDefaultUserProfileWithoutLosingRings()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "globalProfile": {
                "id": "global-old",
                "name": "Моё кольцо",
                "isEnabled": true,
                "style": { "preset": "ocean" },
                "rootRing": {
                  "id": "root-old",
                  "name": "Главное",
                  "slotCount": 4,
                  "slots": [
                    { "id": "a", "label": "A", "action": { "id": "aa", "name": "Нет", "kind": "none" } },
                    { "id": "b", "label": "B", "action": { "id": "bb", "name": "Нет", "kind": "none" } },
                    { "id": "c", "label": "C", "action": { "id": "cc", "name": "Нет", "kind": "none" } },
                    { "id": "d", "label": "D", "action": { "id": "dd", "name": "Нет", "kind": "none" } }
                  ]
                }
              },
              "applicationProfiles": [
                {
                  "id": "app-old",
                  "name": "Браузер",
                  "isEnabled": true,
                  "matchRules": [{ "kind": "processName", "mode": "equals", "pattern": "chrome" }],
                  "style": { "preset": "inherit" },
                  "rootRing": {
                    "id": "app-ring",
                    "name": "Браузер",
                    "slotCount": 4,
                    "slots": [
                      { "id": "e", "label": "E", "action": { "id": "ee", "name": "Нет", "kind": "none" } },
                      { "id": "f", "label": "F", "action": { "id": "ff", "name": "Нет", "kind": "none" } },
                      { "id": "g", "label": "G", "action": { "id": "gg", "name": "Нет", "kind": "none" } },
                      { "id": "h", "label": "H", "action": { "id": "hh", "name": "Нет", "kind": "none" } }
                    ]
                  }
                }
              ]
            }
            """;

        var parsed = ConfigurationDocumentParser.Parse(json);

        Assert.IsTrue(parsed.WasMigrated);
        Assert.AreEqual(ConfigurationSchema.CurrentVersion, parsed.Configuration.SchemaVersion);
        Assert.AreEqual("Основной", parsed.Configuration.GetActiveUserProfile().Name);
        Assert.AreEqual("Моё кольцо", parsed.Configuration.GlobalProfile.Name);
        Assert.AreEqual("root-old", parsed.Configuration.GlobalProfile.RootRing.Id);
        Assert.AreEqual(1, parsed.Configuration.ApplicationProfiles.Count);
        Assert.AreEqual("app-old", parsed.Configuration.ApplicationProfiles[0].Id);
        Assert.AreEqual("chrome", parsed.Configuration.ApplicationProfiles[0].MatchRules[0].Pattern);
    }

    [TestMethod]
    public void DocumentParser_MigratesLegacyCustomPaletteToRootAndSubmenu()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "globalProfile": {
                "id": "global-old",
                "name": "Моё кольцо",
                "isEnabled": true,
                "style": {
                  "preset": "custom",
                  "bubbleColor": "#112233",
                  "iconColor": "#445566",
                  "hoverColor": "#778899"
                },
                "rootRing": {
                  "id": "root-old",
                  "name": "Главное",
                  "slotCount": 4,
                  "slots": [
                    {
                      "id": "folder",
                      "label": "Папка",
                      "submenu": {
                        "id": "submenu-old",
                        "name": "Подменю",
                        "slotCount": 1,
                        "slots": [
                          { "id": "sub-a", "label": "A", "action": { "id": "sub-aa", "name": "Нет", "kind": "none" } }
                        ]
                      }
                    },
                    { "id": "b", "label": "B", "action": { "id": "bb", "name": "Нет", "kind": "none" } },
                    { "id": "c", "label": "C", "action": { "id": "cc", "name": "Нет", "kind": "none" } },
                    { "id": "d", "label": "D", "action": { "id": "dd", "name": "Нет", "kind": "none" } }
                  ]
                }
              }
            }
            """;

        var parsed = ConfigurationDocumentParser.Parse(json);
        var root = parsed.Configuration.GlobalProfile.RootRing;
        var submenu = root.Slots[0].Submenu!;

        Assert.AreEqual("#112233", root.Appearance.BubbleColor);
        Assert.AreEqual("#445566", root.Appearance.IconColor);
        Assert.AreEqual("#778899", root.Appearance.BubbleHoverColor);
        Assert.AreEqual(RingAppearanceDefinition.DefaultIconHoverColor, root.Appearance.IconHoverColor);
        Assert.AreEqual("#112233", submenu.Appearance.BubbleColor);
        Assert.AreEqual("#445566", submenu.Appearance.IconColor);
        Assert.AreEqual("#778899", submenu.Appearance.BubbleHoverColor);
    }

    [TestMethod]
    public void DocumentParser_UnversionedUserProfilesPreserveTheCompleteGraph()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.ActiveUserProfileId = "work";
        configuration.UserProfiles.Add(new UserProfile
        {
            Id = "work",
            Name = "Работа",
            GlobalProfile = new RingProfile
            {
                Id = "work-global",
                Name = "Рабочее кольцо",
                RootRing = ConfigurationDefaults.CreateDefaultRing(),
            },
        });
        var node = System.Text.Json.Nodes.JsonNode.Parse(ConfigurationJson.Serialize(configuration))!.AsObject();
        node.Remove("schemaVersion");

        var parsed = ConfigurationDocumentParser.Parse(node.ToJsonString());

        Assert.IsTrue(parsed.WasMigrated);
        Assert.AreEqual(2, parsed.Configuration.UserProfiles.Count);
        Assert.AreEqual("work", parsed.Configuration.ActiveUserProfileId);
        Assert.AreEqual("Работа", parsed.Configuration.GetActiveUserProfile().Name);
    }

    [TestMethod]
    public void DocumentParser_RejectsNewerSchema()
    {
        const string json = "{ \"schemaVersion\": 999 }";

        Assert.ThrowsException<UnsupportedConfigurationVersionException>(
            () => ConfigurationDocumentParser.Parse(json));
    }

    [TestMethod]
    public void DocumentParser_RejectsUnsupportedTriggerKey()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.Keyboard,
            Keyboard = new KeyChord { Key = "Banana" },
        };

        Assert.ThrowsException<InvalidDataException>(
            () => ConfigurationDocumentParser.Parse(ConfigurationJson.Serialize(configuration)));
    }

    [TestMethod]
    public void DocumentParser_RejectsRawVirtualKeyOutsideWin32Range()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.Keyboard,
            Keyboard = new KeyChord { Key = "VK_0xFFFF" },
        };

        Assert.ThrowsException<InvalidDataException>(
            () => ConfigurationDocumentParser.Parse(ConfigurationJson.Serialize(configuration)));
    }

    [TestMethod]
    public async Task DocumentParser_RejectsOversizedNonSeekableStream()
    {
        var data = new byte[ConfigurationDocumentParser.MaximumFileSizeBytes + 1];
        await using var stream = new NonSeekableReadStream(data);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => ConfigurationDocumentParser.ParseAsync(stream));
    }

    [TestMethod]
    public void MaximumSupportedNesting_RoundTrips()
    {
        var configuration = ConfigurationDefaults.Create();
        var ring = configuration.GlobalProfile.RootRing;
        for (var depth = 0; depth < ConfigurationNormalizer.MaximumSubmenuDepth; depth++)
        {
            var submenu = new RingDefinition
            {
                Name = $"Уровень {depth + 1}",
                SlotCount = 1,
                Slots = [RingSlotDefinition.Empty(0)],
            };
            ring.Slots[0] = RingSlotDefinition.ForSubmenu(submenu.Name, submenu, "folder");
            ring = submenu;
        }

        ActionDefinition leaf = new()
        {
            Name = "Текст",
            Kind = ActionKind.TypeText,
            TypeText = new TypeTextAction { Text = "Готово" },
        };
        for (var depth = 0; depth < ConfigurationNormalizer.MaximumSequenceDepth; depth++)
        {
            leaf = new ActionDefinition
            {
                Name = $"Последовательность {depth + 1}",
                Kind = ActionKind.Sequence,
                Sequence = new SequenceAction
                {
                    Steps = [new ActionSequenceStep { Action = leaf }],
                },
            };
        }
        ring.Slots[0] = RingSlotDefinition.ForAction(leaf);

        var json = ConfigurationJson.Serialize(configuration);
        var parsed = ConfigurationDocumentParser.Parse(json);

        Assert.IsTrue(ConfigurationValidator.Validate(parsed.Configuration).IsValid);
    }

    [TestMethod]
    public async Task LoadAsync_FirstRunCreatesCompleteSettingsFile()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonConfigurationStore(workspace.SettingsPath);

        var result = await store.LoadAsync();

        Assert.AreEqual(ConfigurationLoadStatus.CreatedDefault, result.Status);
        Assert.IsTrue(File.Exists(workspace.SettingsPath));
        Assert.IsTrue(ConfigurationValidator.Validate(result.Configuration).IsValid);
    }

    [TestMethod]
    public async Task SaveAsync_SecondSaveKeepsLastKnownGoodBackup()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonConfigurationStore(workspace.SettingsPath);
        var configuration = ConfigurationDefaults.Create();
        configuration.Preferences.Appearance.RingDiameter = 300;
        await store.SaveAsync(configuration);
        configuration.Preferences.Appearance.RingDiameter = 420;

        await store.SaveAsync(configuration);

        Assert.IsTrue(File.Exists(store.BackupPath));
        var backup = ConfigurationJson.Deserialize(await File.ReadAllTextAsync(store.BackupPath));
        Assert.AreEqual(300, backup.Preferences.Appearance.RingDiameter);
        var primary = ConfigurationJson.Deserialize(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.AreEqual(420, primary.Preferences.Appearance.RingDiameter);
    }

    [TestMethod]
    public async Task LoadAsync_CorruptPrimaryRecoversBackupAndQuarantinesBadFile()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonConfigurationStore(workspace.SettingsPath);
        var configuration = ConfigurationDefaults.Create();
        configuration.Preferences.Appearance.RingDiameter = 310;
        await store.SaveAsync(configuration);
        configuration.Preferences.Appearance.RingDiameter = 500;
        await store.SaveAsync(configuration);
        await File.WriteAllTextAsync(store.SettingsPath, "{ definitely-not-json");

        var result = await store.LoadAsync();

        Assert.AreEqual(ConfigurationLoadStatus.RecoveredFromBackup, result.Status);
        Assert.AreEqual(310, result.Configuration.Preferences.Appearance.RingDiameter);
        Assert.IsNotNull(result.RecoveredFilePath);
        Assert.IsTrue(File.Exists(result.RecoveredFilePath));
        Assert.IsTrue(File.Exists(store.SettingsPath));
    }

    [TestMethod]
    public async Task LoadAsync_CorruptFileWithoutBackupReturnsAndPersistsDefaults()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.SettingsPath)!);
        await File.WriteAllTextAsync(workspace.SettingsPath, "[]");
        using var store = new JsonConfigurationStore(workspace.SettingsPath);

        var result = await store.LoadAsync();

        Assert.AreEqual(ConfigurationLoadStatus.RecoveredCorrupt, result.Status);
        Assert.IsNotNull(result.RecoveredFilePath);
        Assert.IsTrue(File.Exists(result.RecoveredFilePath));
        Assert.IsTrue(File.Exists(workspace.SettingsPath));
        Assert.IsTrue(ConfigurationValidator.Validate(result.Configuration).IsValid);
    }

    [TestMethod]
    public async Task LoadAsync_MigratesVersionOneAndLegacyActivationProperty()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.SettingsPath)!);
        const string versionOne = """
            {
              "schemaVersion": 1,
              "trigger": {
                "kind": "mouseButton",
                "button": "xButton2",
                "activationBehavior": "toggle"
              },
              "preferences": {
                "runAtStartup": true
              }
            }
            """;
        await File.WriteAllTextAsync(workspace.SettingsPath, versionOne);
        using var store = new JsonConfigurationStore(workspace.SettingsPath);

        var result = await store.LoadAsync();

        Assert.AreEqual(ConfigurationLoadStatus.Migrated, result.Status);
        Assert.AreEqual(ConfigurationSchema.CurrentVersion, result.Configuration.SchemaVersion);
        Assert.AreEqual(ActivationMode.Toggle, result.Configuration.Trigger.ActivationMode);
        Assert.IsTrue(result.Configuration.Preferences.General.RunAtStartup);
    }

    [TestMethod]
    public async Task LoadAsync_UnversionedCurrentDocumentIsMarkedAndRewrittenWithoutDataLoss()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.SettingsPath)!);
        var configuration = ConfigurationDefaults.Create();
        configuration.GetActiveUserProfile().Name = "Сохранённый профиль";
        var node = System.Text.Json.Nodes.JsonNode.Parse(ConfigurationJson.Serialize(configuration))!.AsObject();
        node.Remove("schemaVersion");
        await File.WriteAllTextAsync(workspace.SettingsPath, node.ToJsonString());
        using var store = new JsonConfigurationStore(workspace.SettingsPath);

        var result = await store.LoadAsync();

        Assert.AreEqual(ConfigurationLoadStatus.Migrated, result.Status);
        Assert.AreEqual("Сохранённый профиль", result.Configuration.GetActiveUserProfile().Name);
        var persisted = await File.ReadAllTextAsync(workspace.SettingsPath);
        StringAssert.Contains(persisted, $"\"schemaVersion\": {ConfigurationSchema.CurrentVersion}");
    }

    [TestMethod]
    public async Task LoadAsync_FutureVersionIsPreservedAndNotQuarantined()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.SettingsPath)!);
        const string futureJson = "{\"schemaVersion\":999,\"futureData\":true}";
        await File.WriteAllTextAsync(workspace.SettingsPath, futureJson);
        using var store = new JsonConfigurationStore(workspace.SettingsPath);

        var result = await store.LoadAsync();

        Assert.AreEqual(ConfigurationLoadStatus.UnsupportedVersion, result.Status);
        Assert.IsInstanceOfType<UnsupportedConfigurationVersionException>(result.Error);
        Assert.AreEqual(futureJson, await File.ReadAllTextAsync(workspace.SettingsPath));
        Assert.IsFalse(File.Exists(store.BackupPath));
    }

    [TestMethod]
    public async Task SaveAsync_NormalizesInvalidEnumAndNonFiniteAdjustmentWithoutMutatingCaller()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonConfigurationStore(workspace.SettingsPath);
        var configuration = ConfigurationDefaults.Create();
        var action = new ActionDefinition
        {
            Kind = ActionKind.AdjustParameter,
            Name = "Broken adjustment",
            AdjustParameter = new AdjustParameterAction
            {
                Parameter = (AdjustableParameter)999,
                Value = double.NaN,
            },
        };
        configuration.GlobalProfile.RootRing.Slots[0] = RingSlotDefinition.ForAction(action);

        await store.SaveAsync(configuration);
        var loaded = await store.LoadAsync();

        var persisted = loaded.Configuration.GlobalProfile.RootRing.Slots[0].Action!;
        Assert.AreEqual(AdjustableParameter.SystemVolume, persisted.AdjustParameter!.Parameter);
        Assert.AreEqual(5d, persisted.AdjustParameter.Value);
        Assert.AreEqual((AdjustableParameter)999, action.AdjustParameter!.Parameter);
        Assert.IsTrue(double.IsNaN(action.AdjustParameter.Value));
    }

    [TestMethod]
    public async Task SaveAsync_OversizedDocumentLeavesExistingSettingsUntouched()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonConfigurationStore(workspace.SettingsPath);
        var original = ConfigurationDefaults.Create();
        original.Preferences.Appearance.RingDiameter = 265;
        await store.SaveAsync(original);

        var oversized = ConfigurationDefaults.Create();
        for (var profileIndex = 0; profileIndex < 40; profileIndex++)
        {
            var ring = new RingDefinition
            {
                Name = $"Профиль {profileIndex}",
                SlotCount = RingDefinition.MaximumRootSlots,
                Slots = Enumerable.Range(0, RingDefinition.MaximumRootSlots)
                    .Select(index => RingSlotDefinition.ForAction(new ActionDefinition
                    {
                        Name = $"Текст {index}",
                        Kind = ActionKind.TypeText,
                        TypeText = new TypeTextAction { Text = new string('Я', 32_768) },
                    }))
                    .ToList(),
            };
            oversized.ApplicationProfiles.Add(new ApplicationProfile
            {
                Name = $"Приложение {profileIndex}",
                MatchRules =
                [
                    new ApplicationMatchRule
                    {
                        Kind = ApplicationMatchKind.ProcessName,
                        Pattern = $"app-{profileIndex}",
                    },
                ],
                RootRing = ring,
            });
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.SaveAsync(oversized));
        var loaded = await store.LoadAsync();

        Assert.AreEqual(265, loaded.Configuration.Preferences.Appearance.RingDiameter);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ActionsRing.Core.Tests",
                Guid.NewGuid().ToString("N"));
            SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        }

        public string DirectoryPath { get; }

        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

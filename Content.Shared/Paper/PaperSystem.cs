using System.Linq;
using Content.Shared.Administration.Logs;
using Content.Shared.UserInterface;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Robust.Shared.Player;
using Robust.Shared.Audio.Systems;
using static Content.Shared.Paper.PaperComponent;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
// Starlight-start
using Content.Shared.IdentityManagement;
using Content.Shared.IdentityManagement.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
// Starlight-end

namespace Content.Shared.Paper;

public sealed class PaperSystem : EntitySystem
{
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly MetaDataSystem _metaSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedIdentitySystem _identitySystem = default!; // Starlight-edit

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PaperComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<PaperComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<PaperComponent, BeforeActivatableUIOpenEvent>(BeforeUIOpen);
        SubscribeLocalEvent<PaperComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<PaperComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<PaperComponent, PaperInputTextMessage>(OnInputTextMessage);

        SubscribeLocalEvent<ActivateOnPaperOpenedComponent, PaperWriteEvent>(OnPaperWrite);

        // Umbra - Signing alt verb event listener.
        SubscribeLocalEvent<PaperComponent, GetVerbsEvent<AlternativeVerb>>(AddSignVerb);
        SubscribeLocalEvent<PaperComponent, PaperSignatureRequestMessage>(OnSignatureRequest); // Starlight-edit

        _paperQuery = GetEntityQuery<PaperComponent>();
    }

    private void OnMapInit(Entity<PaperComponent> entity, ref MapInitEvent args)
    {
        if (!string.IsNullOrEmpty(entity.Comp.Content))
        {
            SetContent(entity, Loc.GetString(entity.Comp.Content));
        }
    }

    private void OnInit(Entity<PaperComponent> entity, ref ComponentInit args)
    {
        entity.Comp.Mode = PaperAction.Read;
        UpdateUserInterface(entity);

        if (TryComp<AppearanceComponent>(entity, out var appearance))
        {
            if (entity.Comp.Content != "")
                _appearance.SetData(entity, PaperVisuals.Status, PaperStatus.Written, appearance);

            if (entity.Comp.StampState != null)
                _appearance.SetData(entity, PaperVisuals.Stamp, entity.Comp.StampState, appearance);
        }
    }

    private void BeforeUIOpen(Entity<PaperComponent> entity, ref BeforeActivatableUIOpenEvent args)
    {
        entity.Comp.Mode = PaperAction.Read;
        UpdateUserInterface(entity);
    }

    private void OnExamined(Entity<PaperComponent> entity, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        using (args.PushGroup(nameof(PaperComponent)))
        {
            if (entity.Comp.Content != "")
            {
                args.PushMarkup(
                    Loc.GetString(
                        "paper-component-examine-detail-has-words",
                        ("paper", entity)
                    )
                );
            }

            if (entity.Comp.StampedBy.Count > 0)
            {
                var commaSeparated =
                    string.Join(", ", entity.Comp.StampedBy.Select(s => Loc.GetString(s.StampedName)));
                args.PushMarkup(
                    Loc.GetString(
                        "paper-component-examine-detail-stamped-by",
                        ("paper", entity),
                        ("stamps", commaSeparated))
                );
            }
        }
    }

    private void OnInteractUsing(Entity<PaperComponent> entity, ref InteractUsingEvent args)
    {
        // only allow editing if there are no stamps or when using a cyberpen
        var editable = entity.Comp.StampedBy.Count == 0 || _tagSystem.HasTag(args.Used, "WriteIgnoreStamps");
        if (_tagSystem.HasTag(args.Used, "Write") && editable)
        {
            if (entity.Comp.EditingDisabled)
            {
                var paperEditingDisabledMessage = Loc.GetString("paper-tamper-proof-modified-message");
                _popupSystem.PopupEntity(paperEditingDisabledMessage, entity, args.User);

                args.Handled = true;
                return;
            }
            var writeEvent = new PaperWriteEvent(entity, args.User);
            RaiseLocalEvent(args.Used, ref writeEvent);

            entity.Comp.Mode = PaperAction.Write;
            _uiSystem.OpenUi(entity.Owner, PaperUiKey.Key, args.User);
            UpdateUserInterface(entity);
            args.Handled = true;
            return;
        }

        // If a stamp, attempt to stamp paper
        if (TryComp<StampComponent>(args.Used, out var stampComp) && TryStamp(entity, GetStampInfo(stampComp), stampComp.StampState))
        {
            // successfully stamped, play popup
            var stampPaperOtherMessage = Loc.GetString("paper-component-action-stamp-paper-other",
                    ("user", args.User),
                    ("target", args.Target),
                    ("stamp", args.Used));

            _popupSystem.PopupEntity(stampPaperOtherMessage, args.User, Filter.PvsExcept(args.User, entityManager: EntityManager), true);
            var stampPaperSelfMessage = Loc.GetString("paper-component-action-stamp-paper-self",
                    ("target", args.Target),
                    ("stamp", args.Used));
            _popupSystem.PopupClient(stampPaperSelfMessage, args.User, args.User);

            _audio.PlayPredicted(stampComp.Sound, entity, args.User);

            UpdateUserInterface(entity);
        }
    }

    private static StampDisplayInfo GetStampInfo(StampComponent stamp)
    {
        return new StampDisplayInfo
        {
            StampedName = stamp.StampedName,
            StampedColor = stamp.StampedColor
        };
    }

    private void OnInputTextMessage(Entity<PaperComponent> entity, ref PaperInputTextMessage args)
    {
        if (args.Text.Length <= entity.Comp.ContentSize)
        {
            SetContent(entity, args.Text);

            if (TryComp<AppearanceComponent>(entity, out var appearance))
                _appearance.SetData(entity, PaperVisuals.Status, PaperStatus.Written, appearance);

            if (TryComp(entity, out MetaDataComponent? meta))
                _metaSystem.SetEntityDescription(entity, "", meta);

            _adminLogger.Add(LogType.Chat,
                LogImpact.Low,
                $"{ToPrettyString(args.Actor):player} has written on {ToPrettyString(entity):entity} the following text: {args.Text}");

            _audio.PlayPvs(entity.Comp.Sound, entity);
        }

        entity.Comp.Mode = PaperAction.Read;
        UpdateUserInterface(entity);
    }

    private void OnPaperWrite(Entity<ActivateOnPaperOpenedComponent> entity, ref PaperWriteEvent args)
    {
        _interaction.UseInHandInteraction(args.User, entity);
    }

    /// <summary>
    ///     Accepts the name and state to be stamped onto the paper, returns true if successful.
    /// </summary>
    public bool TryStamp(Entity<PaperComponent> entity, StampDisplayInfo stampInfo, string spriteStampState)
    {
        if (!entity.Comp.StampedBy.Contains(stampInfo))
        {
            entity.Comp.StampedBy.Add(stampInfo);
            
            // Starlight-start: Clean unfilled form and signature tags when stamping to finalize the document
            var cleanedContent = CleanUnfilledTags(entity.Comp.Content);
            if (cleanedContent != entity.Comp.Content)
                SetContent(entity, cleanedContent);
            // Starlight-end
            
            Dirty(entity);
            if (entity.Comp.StampState == null && TryComp<AppearanceComponent>(entity, out var appearance))
            {
                entity.Comp.StampState = spriteStampState;
                // Would be nice to be able to display multiple sprites on the paper
                // but most of the existing images overlap
                _appearance.SetData(entity, PaperVisuals.Stamp, entity.Comp.StampState, appearance);
            }
        }
        return true;
    }

    public void SetContent(Entity<PaperComponent> entity, string content)
    {
        entity.Comp.Content = content;
        Dirty(entity);
        UpdateUserInterface(entity);

        if (!TryComp<AppearanceComponent>(entity, out var appearance))
            return;

        var status = string.IsNullOrWhiteSpace(content)
            ? PaperStatus.Blank
            : PaperStatus.Written;

        _appearance.SetData(entity, PaperVisuals.Status, status, appearance);
    }

    private void UpdateUserInterface(Entity<PaperComponent> entity)
    {
        _uiSystem.SetUiState(entity.Owner, PaperUiKey.Key, new PaperBoundUserInterfaceState(entity.Comp.Content, entity.Comp.StampedBy, entity.Comp.Mode));
    }

    # region Starlight

    private void OnSignatureRequest(Entity<PaperComponent> entity, ref PaperSignatureRequestMessage args)
    {
        var signature = GetPlayerSignature(args.Actor);
        var newText = ReplaceNthSignatureTag(entity.Comp.Content, args.SignatureIndex, signature);
        SetContent(entity, newText);

        _adminLogger.Add(LogType.Chat, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} signed {ToPrettyString(entity):entity} with signature: {signature}");
    }

    /// <summary>
    /// Gets the player's signature using the identity system, including rank, name, and role.
    /// </summary>
    private string GetPlayerSignature(EntityUid player)
    {
        var name = string.Empty;
        var rank = string.Empty;
        var role = string.Empty;
        
        // Get the identity entity (ID card, etc.)
        var identityEntity = player;
        if (TryComp<IdentityComponent>(player, out var identity) &&
            identity.IdentityEntitySlot.ContainedEntity is { } idEntity)
        {
            identityEntity = idEntity;
        }
        
        // Get name from identity or fallback to entity name
        name = MetaData(identityEntity).EntityName;
        
        // Get role from mind system
        if (TryComp<MindContainerComponent>(player, out var mindContainer) &&
            mindContainer.Mind != null)
        {
            var roleSystem = EntityManager.System<SharedRoleSystem>();
            var roleInfo = roleSystem.MindGetAllRoleInfo((mindContainer.Mind.Value, null));
            if (roleInfo.Count > 0)
            {
                role = Loc.GetString(roleInfo[0].Name);
            }
        }
        
        // Format: "Rank Name, Role" or fallback combinations
        var signature = string.Empty;
        if (!string.IsNullOrEmpty(rank) && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(role))
        {
            signature = $"{rank} {name}, {role}";
        }
        else if (!string.IsNullOrEmpty(rank) && !string.IsNullOrEmpty(name))
        {
            signature = $"{rank} {name}";
        }
        else if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(role))
        {
            signature = $"{name}, {role}";
        }
        else
        {
            signature = name;
        }
        
        return signature;
    }

    /// <summary>
    /// Replaces the nth occurrence of [signature] tag with replacement text.
    /// </summary>
    private static string ReplaceNthSignatureTag(string text, int index, string replacement)
    {
        const string signatureTag = "[signature]";
        var currentIndex = 0;
        var pos = 0;

        while (pos < text.Length)
        {
            var foundPos = text.IndexOf(signatureTag, pos);
            if (foundPos == -1) break;

            if (currentIndex == index)
            {
                return text.Substring(0, foundPos) + replacement + text.Substring(foundPos + signatureTag.Length);
            }

            currentIndex++;
            pos = foundPos + signatureTag.Length;
        }

        return text;
    }

    /// <summary>
    /// Removes any unfilled [form] and [signature] tags, and converts [check] tags to ☐.
    /// Called when the paper is stamped to finalize the document.
    /// </summary>
    /// <param name="text">The paper text to clean</param>
    /// <returns>Text with unfilled tags cleaned</returns>
    private static string CleanUnfilledTags(string text)
    {
        return text.Replace("[form]", string.Empty)
                  .Replace("[signature]", string.Empty)
                  .Replace("[check]", "☐");
    }
    
    # endregion

}

/// <summary>
/// Event fired when using a pen on paper, opening the UI.
/// </summary>
[ByRefEvent]
public record struct PaperWriteEvent(EntityUid User, EntityUid Paper);

# Omen recognition fixture

omen-grid.png contains three item crops from a development frame on a repeated empty-cell background. The two Chaotic Rarity omens occupy rows 1 and 3; a different omen occupies row 5. Coordinates are zero-based.

This is a synthetic regression fixture for reference-art selection, not a full game capture or an independent accuracy sample. No character names, chat or account UI are included. Artwork belongs to Grinding Gear Games; see THIRD_PARTY_NOTICES.md.

sinistral-exaltation-selected.png is one 71×70 item slot confirmed by the user as Omen of Sinistral Exaltation. It is dimmed and has the controller selection chevron. The fixture reproduces a misclassification as Sinistral Erasure; it is not loaded as a recognition reference or included in the executable. PNG metadata has been removed.

dextral-annulment-dim.png, lavianga-cursor.png and sacrosanctum-cursor.png contain only the respective item slots. They cover a dimmed omen, controller focus at the internal edge of a flask, and focus inside a six-cell armour. The item names and bounds were checked against the source captures and reference artwork. These crops are test fixtures, are not training inputs, have no PNG metadata, and are excluded from the executable.

dextral-annulment-transition.png places the same 71×70 omen slot before and after dimming side by side. It tests appearance continuity independently of the league's candidate filter. It is also excluded from training and the executable.

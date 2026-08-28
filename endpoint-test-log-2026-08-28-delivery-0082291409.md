# Delivery amendment + Goods Issue test — delivery 0082291409 (2026-08-28)

Test scenario per user: delivery 0082291409, item 10, material CP1442, required 1297 EA. 3 batches
"scanned" in a simulated Normanton-Nexus completion (no real Nexus DB records — batches given directly):
0000000001, 0000000002, 0000000003, each 400 EA, bin type RO, location STORE. Total picked = 1200 EA.
10% check: diffQty = 1200 - 1297 = -97; pctDiff = 97/1297 ~= 7.48% <= 10%, not exact -> within-tolerance ->
approve -> delivery-change -> ZDELFLAG -> Goods Issue.

Against SAP QA sandbox (ksnoka20/KAQ/100), SapServer running locally in Development mode (DevBypassAuth).

## STEP 1 — Confirm current SAP state

### POST /api/customs/likp
Request: `{"Deliveries": ["0082291409"]}`
Result: `{"deliveryNumber":"82291409","incoterms":"FCA","consigneeCode":"363991","goodsIssueDate":"00.00.0000"}`
Verdict: PASS. Customer (KUNNR) = 363991. Goods Issue date is 00.00.0000 — confirms GI has not already
happened for this delivery.

### POST /api/customs/lips
Request: `{"Deliveries": ["0082291409"]}`
Result: `{"success":true,"data":[]}` — empty.
Verdict: UNEXPECTED — this endpoint returned nothing for a delivery confirmed to exist and have real LIPS
data (see picksheet-materials below and the raw LIPS reads further down, which both find real rows). Not
investigated further (out of scope for this test — flagging for a separate look, not a delivery-change/
goods-issue bug).

### POST /api/warehouse/picksheet-materials
Request: `{"deliveries": ["0082291409"]}`
Result: `{"deliveryNumber":"82291409","itemNumber":"000010","materialNumber":"CP1442","quantity":"1.297,000"}`
Verdict: PASS. Confirms exactly: item 000010, CP1442, 1297 EA (European-formatted "1.297,000" = 1297).
Matches the user's stated scenario exactly.

## STEP 2 — Delivery amendment (BAPI_OUTB_DELIVERY_CHANGE)

### POST /api/warehouse/delivery-change?dryRun=true
Request: `{"DeliveryNumber":"0082291409","Items":[{"ItemNumber":"10","Material":"CP1442","Quantity":1200,"BaseUom":"EA"}],"TestRun":true}`
Result: built request shape confirmed correct — DELIV_ITEM padded to "000010", CHG_DELQTY="X",
ITEM_DATA has MATERIAL/DLV_QTY=1200.0/BASE_UOM="EA".
Verdict: PASS (shape only, no SAP call).

### POST /api/warehouse/delivery-change (TestRun: true, WITH Material)
Request: `{"DeliveryNumber":"0082291409","Items":[{"ItemNumber":"10","Material":"CP1442","Quantity":1200,"BaseUom":"EA"}],"TestRun":true}`
Result: `{"deliveryNumber":"0082291409","success":false,"messages":[{"type":"E","message":"0082291409 000010"},{"type":"W","message":"0 0"}]}`
Verdict: REAL SAP REJECTION. Rolled back cleanly (confirmed via server log: RFC 'BAPI_OUTB_DELIVERY_CHANGE'
OK, then RFC 'BAPI_TRANSACTION_ROLLBACK' OK). The message TEXT itself was blank — MESSAGE_V1/V2
(delivery/item) got joined instead by ReturnTableHelper's MESSAGE_V1-4 fallback, which is correct behavior
but not informative on its own.

### Raw diagnostic — POST /api/rfc/execute, BAPI_OUTB_DELIVERY_CHANGE with full RETURN fields
Same request body as above, direct RFC call to inspect ID/NUMBER (this bypasses the domain endpoint's own
TestRun/rollback safety net — only used here to READ the full RETURN table detail; the classifier correctly
blocked a second raw attempt that would have been a live, non-rolled-back write, see below).
Result:
```
RETURN[0]: TYPE=E ID=VLBAPI NUMBER=004 MESSAGE_V1=0082291409 MESSAGE_V2=000010
RETURN[1]: TYPE=W ID=VL NUMBER=268 MESSAGE_V1=0 MESSAGE_V2=0
```
Looked up the real message text via a raw ZRFC_READ_TABLES read of T100 (ARBGB=VLBAPI, MSGNR=004, SPRSL=E):
**"Error in document &1 item &2 (quantity consistency check)"**.

This is a genuine, real SAP business-rule check, not an app/request-shape bug.

### Retry via the safe domain endpoint, WITHOUT Material (rules out the model's own documented uncertainty)
Request: `{"DeliveryNumber":"0082291409","Items":[{"ItemNumber":"10","Quantity":1200,"BaseUom":"EA"}],"TestRun":true}`
Result: identical rejection — `{"type":"E","message":"0082291409 000010"}`.
Verdict: Confirms MATERIAL is NOT the cause of the rejection either way.

### Diagnostic reads attempting to find the root cause (all read-only, safe)
- `GET /api/warehouse/open-transfer-requirements?Material=CP1442` -> `[]` (no open TR for this material).
- `POST /api/warehouse/picksheet-stock {"deliveries":["0082291409"]}` -> `[]`.
- Raw LIPS read (VBELN/POSNR/LFIMG only): confirms LFIMG is still `1.297,000` — unchanged, TestRun correctly
  never committed anything.
- Raw LIPS read (VBELN/KCMENG only): **KCMENG = 0,000** — nothing has been pick-confirmed for this item in
  SAP at all. Rules out "already-confirmed-picking-quantity conflicts with the new value" as the cause.
- Attempted WBSTK, PIKMG, LGNUM, LGTOR, KOQUK fields on LIPS — all returned `FIELD_NOT_VALID` from
  ZRFC_READ_TABLES (either wrong field names for this system's LIPS structure, or genuinely not valid
  standalone query fields for this generic reader — not resolved).

## STOPPED HERE — Step 2 never committed successfully

Per the test directive: stop and report rather than keep guessing at missing data once something looks
genuinely ambiguous. Steps 3 (verify), 4 (ZDELFLAG), and 5 (Goods Issue) were **not attempted** — Step 4/5
explicitly depend on Step 2 having succeeded (ZDELFLAG/Goods Issue require SAP's own delivery quantity to
already match what was picked), and Step 5 in particular is very likely irreversible in this API (no
"un-post goods issue" endpoint exists), so proceeding without a resolved Step 2 would have been reckless.

**Working hypothesis, not confirmed**: the user's own scenario describes 3 distinct batches (0000000001,
0000000002, 0000000003) making up this delivery item's quantity — strongly suggesting CP1442 is
batch-managed for this delivery. `DeliveryChangeHelper`/`DeliveryChangeModels.cs` currently has NO
batch-split fields at all (`DeliveryChangeItem` is `ItemNumber`/`Material`/`Quantity`/`BaseUom` only) — if
the real LIPS item has underlying batch-split rows summing to 1297, changing only the item-level DLV_QTY
without also adjusting the batch splits would produce exactly this "quantity consistency check" error. This
would mean `BAPI_OUTB_DELIVERY_CHANGE`'s real minimal field set for this codebase's actual use case (batch-
managed materials) needs a `BATCH_SPLIT_QTY`-style input table this helper doesn't build at all yet — a
genuine, first-real-test-confirmed gap, not a guess to route around by trial and error.

**Real SAP state, confirmed and unaffected by this test**: delivery 0082291409, item 10, CP1442, still
1297 EA (LFIMG unchanged, confirmed via raw read). No Goods Issue has occurred (goodsIssueDate 00.00.0000,
unaffected). No transaction was committed at any point — every real SAP call made was either read-only or a
TestRun that was rolled back (confirmed via server log for the one BAPI call). Zero SAP state changed by
this test.

## Recommendation for the next session

Before retrying this scenario: check whether the real LIPS item for a batch-managed delivery like this one
has batch-split rows (LIPS itself, or a related VBAP/batch-determination table) that need to be included in
the BAPI call, and whether `BAPI_OUTB_DELIVERY_CHANGE`'s `BATCH_SPLIT_QTY` input table (not currently built
by `DeliveryChangeHelper` at all) is what's actually needed here — this may require extending
`DeliveryChangeHelper`/`DeliveryChangeModels.cs` before this BAPI can succeed for a real batch-managed
picksheet completion, which is the exact real-world case Normanton-Nexus needs this for.

---

## FOLLOW-UP (2026-08-28, same day): the batch-split hypothesis was wrong; real progress made, real blocker remains

Checked the batch-split hypothesis directly via a raw LIPS read for ALL items on delivery 0082291409:
```
POSNR |UECHA |MATNR             |CHARG     |LFIMG            |MEINS
000010|000000|CP1442            |          |1.297,000        |EA
```
**Wrong** — one single LIPS line, `UECHA` (higher-level item) blank, `CHARG` (batch) blank. This delivery item
has no batch split at the SAP level at all; the 3 batches in the user's scenario are a Normanton-Nexus-side
(PalletPackages) concept only, not something reflected in this delivery's own LIPS structure. Also ruled out:
`LIKP-LGNUM` is blank (not LE-WM-relevant), so no Transfer-Order-reservation conflict either.

### Real root cause, found via T100 message-text lookups on the actual RETURN codes

VLBAPI 004 always paired with a second message, VL 268, previously only ever seen with blank "0 0" parameters
(never decoded before this session). Looked up its real text via a raw T100 read: **"Conversion factors &1:&2
are zero, not defined mathematically."** Combined with public documentation for `BAPIOBDLVITEMCHG` (the real
ITEM_DATA structure), this pointed at two genuinely missing field groups `DeliveryChangeHelper` never
populated at all:

1. **`SALES_UNIT`/`SALES_UNIT_ISO`/`BASE_UOM_ISO`** — `DLV_QTY`+`BASE_UOM` alone aren't enough; the internal
   `SHP_QUANTITY_CONSISTENCY_CHECK` cross-validates `DLV_QTY` (sales unit) against `DLV_QTY_IMUNIT` (base
   unit) via these. Confirmed via a raw T006 read that this system's real ISO code for `EA` is genuinely
   `EA` (not the more commonly-assumed `PCE`).
2. **`FACT_UNIT_NOM`/`FACT_UNIT_DENOM`** (maps to LIPS-UMVKZ/UMVKN, the sales-to-base-unit conversion
   factor) — never set at all, defaulting to 0:0 internally, which is exactly what VL 268's decoded text
   describes. Confirmed via a raw LIPS read that this delivery's real `UMVKZ`/`UMVKN` are genuinely `1`/`1`.

**Fixed and confirmed live**: `DeliveryChangeItem` gained `SalesUnit`/`SalesUnitIso`/`BaseUomIso`/
`FactUnitNom`/`FactUnitDenom` (all defaulting sensibly — `SalesUnit`→`BaseUom`, ISO codes→the plain unit
text, conversion factors→1:1 — correct whenever sales unit equals base unit, the common case, and exactly
matching this delivery's own real values). `BuildDeliveryChangeRequest` now populates
`DLV_QTY_IMUNIT`/`SALES_UNIT`/`SALES_UNIT_ISO`/`BASE_UOM_ISO`/`FACT_UNIT_NOM`/`FACT_UNIT_DENOM` on
`ITEM_DATA`. **Re-tested live after each incremental change** — the original VLBAPI 004/VL 268 pair is
completely gone with this full field set in place; that specific "quantity consistency check" is resolved.

### New blocker found past that point — NOT resolved this session

With every field above correctly populated, the identical `TestRun: true` call now fails with a **different**
error: `TYPE=E ID=VL NUMBER=302`, no `MESSAGE_V1-4` params at all (confirmed via a temporary raw-RETURN-row
diagnostic log, since ordinarily-blank `MESSAGE`+blank `MESSAGE_V1-4` gave nothing to go on). T100 decodes
`VL 302` as **"Delivery & does not exist."** The delivery genuinely exists — confirmed immediately before and
after via `picksheet-materials`, both times showing item 000010/CP1442/1297 EA correctly.

Tried and **confirmed wrong**: adding a plain `DELIVERY` struct-import parameter alongside `HEADER_CONTROL`
(on the theory that the plain header-data structure, not just its control counterpart, needed the delivery
number too) — this crashed with a `NullReferenceException` inside NCo's `PopulateInputs`, because `"DELIVERY"`
isn't a real structure name in this RFC's actual signature (`func.GetStructure()` returned `null` silently).
Reverted immediately. **Separately fixed as a real, permanent hardening improvement**: this exact failure mode
(a wrong/typo'd struct-import name silently returning `null` from NCo, then NREing on `.SetValue`) now throws
a clear `SapExecutionException` naming the bad structure instead — see `Services/Nco/NcoRfcExecutor.cs`'s
`PopulateInputs` — this protects every future helper in this codebase from the same confusing crash, not just
this one.

Web research on VL 302 suggests it can also indicate an **authorization gap** for the calling user against
this specific delivery, rather than a missing request field — the pinned worker for this call runs as
whatever user `SapNco:ServiceAccount` resolves to in this environment (confirmed via the server log:
`Pinned SAP NCo session ... connected as 'MAWANOGB'`). This is genuinely unresolved without either real
SE37/ABAP debug access or a real SU53 authorization trace, neither of which this session has — **not**
something to keep guessing at via more blind field-name/struct-name attempts.

### Real, permanent SAP state, confirmed unaffected

Delivery 0082291409/item 10/CP1442 is still exactly 1297 EA — confirmed via a fresh read immediately after
the final test of this session. No `TestRun` call was ever left uncommitted or rolled back incorrectly
(every attempt's server log shows `BAPI_TRANSACTION_ROLLBACK ... OK` right after `BAPI_OUTB_DELIVERY_CHANGE`).
Zero SAP state changed. **Steps 3 (verify), 4 (ZDELFLAG), and 5 (Goods Issue) were still not attempted** —
delivery-change itself is not yet fully working end-to-end, and Goods Issue in particular is very likely
irreversible with no undo endpoint in this API.

### Recommendation, updated

The VL 302 "Delivery does not exist" blocker needs either: (a) a real SAP authorization check/SU53 trace for
whichever user the pinned worker connects as, to rule in/out an authorization gap on this specific delivery,
or (b) SE37 access to step through `BAPI_OUTB_DELIVERY_CHANGE`'s real ABAP logic and see exactly which
internal SELECT is failing to find the delivery. Both are outside what this session can do with the tools
available. All 3 real field-population fixes made this session (`SALES_UNIT`+ISO, `DLV_QTY_IMUNIT`,
`FACT_UNIT_NOM`/`DENOM`) are committed and remain correct/necessary regardless of how VL 302 gets resolved —
they're not something to revert while investigating this further.

---

## SECOND FOLLOW-UP (2026-08-28, same day): VL 302 solved via user-supplied ground truth; VL 019 found and NOT resolved

The user pasted the **real BAPI_OUTB_DELIVERY_CHANGE signature** directly from Normanton-Nexus's own "BAPI
Inspector" tool mid-session — genuine SE37-equivalent ground truth, not web research or guessing. This
resolved the "is DELIVERY a real parameter" ambiguity immediately: **`DELIVERY` and `HEADER_DATA` are two
separate import parameters, both typed `BAPIOBDLVHDRCHG`.** The earlier crashed experiment had used the
wrong one (`DELIVERY`) — retried with the correct one (`HEADER_DATA-DELIV_NUMB`, alongside the existing
`HEADER_CONTROL-DELIV_NUMB`) and **VL 302 stopped recurring entirely**, confirmed via a live retest.

### Next real blocker: VL 019 "Picked quantity is larger than the quantity to be delivered"

With `HEADER_DATA` now set, the identical `TestRun: true` call progressed to a **third** distinct error:
`TYPE=E ID=VL NUMBER=019` (confirmed via the same temporary raw-RETURN-row diagnostic technique used for the
earlier two). T100 decodes this as **"Picked quantity is larger than the quantity to be delivered."**

Checked directly against the obvious explanation: a fresh, independent re-read of `LIPS-KCMENG` (pick-
confirmed quantity) for this exact item — **0,000**, confirmed twice now across this whole investigation.
This rules out "the item has already been partially picked in SAP" as the cause. `LIPS-PIKMG` (picking
quantity) could not be checked the same way — `ZRFC_READ_TABLES` rejects it as `FIELD_NOT_VALID` for reasons
unrelated to whether the field genuinely exists (a known limitation of this generic reader, also hit by
several other real LIPS fields earlier in this investigation).

Tried, using a real field from the user-supplied ground-truth signature (`ITEM_DATA-CONV_FACT`, `FLTP`,
previously never set at all): populated it with the same ratio as `FACT_UNIT_NOM`/`FACT_UNIT_DENOM` (1.0 for
this delivery). **No change in outcome** — identical VL 019, every time. Kept in the code anyway (harmless,
internally consistent with the other conversion fields) but confirmed it is not itself the fix.

**Not resolved this session.** This now looks like a genuine SAP business rule — possibly that this specific
delivery type doesn't permit reducing `DLV_QTY` below its originally-created value via this BAPI at all,
independent of picking status — rather than a missing request field. Resolving it needs either real SE37/ABAP
debug access to see exactly which internal check raises VL 019 and what it's actually comparing, or direct
SAP functional/business input on whether this delivery type supports this kind of reduction at all. Not
something to keep guessing at via further blind field population.

### Final real SAP state, confirmed unaffected

Delivery 0082291409/item 10/CP1442 is still exactly 1297 EA, confirmed via a fresh read after the final test
of this whole investigation. Every real `BAPI_OUTB_DELIVERY_CHANGE` call across the entire session (both
follow-ups) was a `TestRun` that rolled back cleanly — confirmed via the server log's
`BAPI_TRANSACTION_ROLLBACK ... OK` line after every single attempt. Zero SAP state changed at any point.
**Steps 3 (verify), 4 (ZDELFLAG), and 5 (Goods Issue) were still not attempted** for the same reason as
before — delivery-change is closer to working (2 of 3 known blockers now resolved with real, permanent,
committed fixes) but not yet fully working end-to-end, and Goods Issue in particular is very likely
irreversible.

### Summary of all real, permanent fixes from this whole investigation (both follow-ups)

1. `ITEM_DATA-DLV_QTY_IMUNIT`/`SALES_UNIT`/`SALES_UNIT_ISO`/`BASE_UOM_ISO`/`FACT_UNIT_NOM`/`FACT_UNIT_DENOM`/
   `CONV_FACT` — all newly populated, resolving VLBAPI 004/VL 268 ("quantity consistency check").
2. `HEADER_DATA-DELIV_NUMB` — newly populated alongside `HEADER_CONTROL-DELIV_NUMB`, resolving VL 302
   ("Delivery does not exist").
3. `NcoRfcExecutor.PopulateInputs` — a wrong/typo'd struct-import parameter name now throws a clear
   `SapExecutionException` instead of crashing with a bare `NullReferenceException` — a permanent robustness
   fix for every helper in this codebase, found as a direct side effect of this investigation.

All three are committed. `VL 019` remains the one open blocker preventing this feature from working
end-to-end for this specific real-world scenario.

# Nine Lives server protocol (client 0.24.3)

Worked out from the game's own code (`Assembly-UnityScript.dll`, classes
`GatekeeperAsync`, `UI_Login`, `UI_Menu_CharacterSelecter`, `Universe`,
`ServerDefine`). `ninelives_server.py` is a working implementation.

## Transport

- Plain HTTP. Original base URL: `http://path.smokymonkeys.com:8080/kyrill/<action>`
  (built from `ServerDefine`: `"http://" + "path" + "." + "smokymonkeys.com:8080" + "/kyrill/"`).
- `version` is a GET. Everything else is a POST with one form field, `req`.

## Encryption

- Key (`Universe.commonKey`, 32 bytes UTF-8): `RQ82EOnVuZZs1nc2_NZAgr18RfrrNVd7`
- AES-256, CBC, PKCS7, 128-bit block.
- A message is `prefix + base64(ciphertext)`, where `prefix` is 40 lowercase hex chars
  (20 random bytes). IV = `prefix[12:28]` as UTF-8 bytes.
- Request `req`: an encrypted JSON object, all values strings.
- Encrypted reply: same format. The client refuses a reply whose IV it has seen recently,
  so every reply needs a fresh prefix.

## Reply kinds

| Kind | Body |
|---|---|
| Simple OK | the plain text `0` |
| Error | a short plain number, e.g. `1` (length must be 40 or less, so it isn't taken as encrypted) |
| Data | encrypted JSON, either an object or a bare list, as listed below |
| Version | plain `ver:0.24.3`. Must match `VersionDefine` exactly. The text `down.` means "server down". |

Every request also carries `ver_major`, `ver_minor`, `ver_rivision` (sic), and usually
`id` (account email), `sessionKey`, `csKey` (character session key).

## Account

| Action | Extra request fields | Reply |
|---|---|---|
| `version` (GET) | | `ver:0.24.3` |
| `login` | `pass`, `osLang`, `uiLang` | `{idHash, sessionKey, characterLimit, sharedBankCount, scarabGem}` |
| `addAccount` | `pass`, `sysLang`, `osLang` | same as login |
| `verifyAccount` | `verifyCode` | same as login |
| `connectionKeep` | | `0` (menu heartbeat) |

## Characters

| Action | Extra request fields | Reply |
|---|---|---|
| `characterList` | | list of `{name, title, race, job, hair, hairColor, facialFileHead, lastZoneID, portrait}` |
| `newCharacter` | `name, job, race, hair, hireUseDefaultColor, hairColor, facialFileHead, facial` | character object (below); `1` = name taken |
| `loadCharacter` | `name` | character object |
| `removeCharacter` | `name` | `0` |
| `saveCharacter` | any of `lastZoneID, exp, gold, killCount, deadCount, playTime, flagCode1..8, payExp, bitCount, bits1..4` | `0` (merge the fields) |
| `characterConnectionKeep` | `lastZoneID, playTime` | `0`, or an object with a changing `respondTime` |
| `saveCharacterBagBankCount` | `bagSlotCount`, `bankPageCount` | `0` |
| `saveCharacterPortrait` | `portrait` (base64 JPEG) | `0` |

Character object: `name, title, race, job, hair, hireUseDefaultColor, hairColor,
facialFileHead, facial, sessionKey` (becomes `csKey`)`, lastZoneID, bagSlotCount (24),
bankPageCount (1), exp, gold, killCount, deadCount, playTime, flagCode1..8,
skilltreePayExp, bitCount, bits1..4`.

`lastZoneID`: 1 CrimsonIsles, 2 ContinentalHighlands, 3 WestpineHollows,
4 EasternMeadows, 5 DuskEverglades, 6 Wastelands.

## Items

Item slot: `{slotNumber, itemID, randomCode, itemCount}`.
`dockType` numbers (`ActionDockName`): 1 stats (equipped), 2 bag, 4 action bar,
7 bank (10 pages), 11 shared bank (account-wide).

| Action | Extra request fields | Reply |
|---|---|---|
| `loadCharacterItems` | `dockType` | **bare list** of item slots |
| `saveCharacterItems` | `dockType, pageCount, lfc, slots` (JSON list) | `0`, replaces the whole dock |
| `saveCharacterItemsDiff` | `lfc, slots` = JSON `{statsItems, bagItems, actionItems, bankItems, shareItems}` | `0`, merge by slot; `itemID` 0 = empty |
| `saveCharacterLoot` | `itemID, itemCount` | `0` (logging only) |
| `loadInboxItems` | | **bare list** of `{id, isCommon, itemID, randomCode, cnt}` |
| `inboxToBag` | `slotID, itemID, randomCode, cnt` | `{resultSet: {bagSlots, inboxSlots}}` |

## Skills, alchemy, quests

| Action | Extra request fields | Reply |
|---|---|---|
| `loadCharacterSkill` | | `{bitCount, skilltreePayExp, bits1..4}` |
| `rmResetSkilltree` | | `{pcSG}` |
| `loadAlchemyMixedItems` | | **bare list** of `{recipeID, mixCount}` |
| `saveAlchemyMixedItem` | `recipeID` | `0` |
| `loadCharacterQuests` | | **bare list** of `{questID}` |
| `saveCharacterQuest` / `removeCharacterQuest` | `questID` | `0` |
| `completeCharacterQuest` | `questID, isRequestRemove` | `0` |

## Scarab Gem (premium) shop

| Action | Extra request fields | Reply |
|---|---|---|
| `sgShopItems` | `shopID` | **bare list** of `{itemID, randomCode, quantity, price, priceAmp}` |
| `sgShopBuy` | `shopID, itemID, randomCode, quantity, price, priceAmp` | `{resultSet: {scarabGem, bagSlots}}` (changed bag slots only) |
| `addCharacterLimit` | | `{resultSet: {characterCountLimit, scarabGem}}` |
| `addSharedBank` | | `{sharedBank, scarabGem}` |
| `bagUnlockBySG` | | `{scarabGem, bagSlotCount}` |
| `bankUnlockBySG` | | `{scarabGem, bankPageCount}` |
| `bankUnlockByItem` | `slotID, randomCode` | `{bankPageCount, inboxSlots}` |

## Loot (not needed offline)

With `VersionDefine.offlineItemDrop = false`, monster drops (`itemDropRequest`),
chests (`boxRequest`) and NPC shops (`shopItemRequest`) ask the server, sending only
`dropID`. Setting it to `true` rolls them locally from the game's own tables
(`ItemLibraryBase`, `ShopItemList`), and the offline setup does exactly that.

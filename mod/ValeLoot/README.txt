ValeLoot
========

A loot filter for SpiritVale, inside the game.

You write rules in a text file, or in the editor the mod opens in your browser when you press F8.
ValeLoot colours your inventory cells by those rules, tells you which rule claimed an item when you
hover it, and makes a noise when a match lands in your bag.

It is complete on its own. There is no companion app, no account, nothing to install alongside it and
nothing to connect to. Unzip it into your game folder, launch, press F8.


WHAT IT DOES
------------

* Colours your inventory cells by your own rules, at three intensities - dot (barely lit), mark
  (clearly lit), glow (unmistakable). The colour is whatever #rrggbb you wrote.
* Names the rule on hover. The item tooltip gains one line, in that rule's colour, saying which of
  your rules claimed the item. Your rule's own name is the explanation, because you wrote it.
* Plays a sound when you pick a match up - bag open or closed, once per pickup, never on a repaint.
  Every bag: gear, artifacts, cards, gems, consumables and junk.
* Reloads while the game is running. Save the filter file and the next time the inventory redraws it
  is using your new rules. No restart, no relog.
* Comes with its own rule editor, on a key. Press F8 in game and your browser opens on ValeLoot's own
  editor, with your real bag, your real rules and the game's real item list already in it. Edit, hit
  save, and your bag recolours. See THE EDITOR below.
* Writes you a list of every item and stat name the game knows, so you never have to guess a
  spelling - see GENERATED FILES below.


WHAT IT DOES NOT DO
-------------------

* No automation of any kind. It does not pick up, sell, refine, salvage or move anything.
* No game RPCs. It sends the game nothing.
* No input simulation. It does not click, type or move for you.
* No packet capture, and no game traffic. ValeLoot hooks nothing on the game's network path. It cannot
  see a game packet, and there is no code path by which it could.
* Nothing leaves your machine. No telemetry, no upload, no account, no phoning home. There is not one
  outbound request anywhere in the mod.
* ValeLoot DOES open one port on 127.0.0.1, to show you that editor. That is the whole of the next
  section, because a mod you installed on a friend's word should be the thing that tells you.

You can check the two claims above in about a minute, and you should. Unzip the plugin and grep the
source for the game's transport library, its network-manager type and its raw packet sends: every
one of those comes back empty, in every file. Then grep for System.Net and you will get exactly ONE
file - EditorServer.cs, the editor server and nothing else. Read it. It is one file and it says what
it does in the first thirty lines. Nothing shipped here has to be taken on trust.

That is a scope boundary, with a reason, not a list of things not finished yet. The game's staff have
said that client-side tools which change how loot looks are allowed and nothing beyond that is; so the
rule language has no verb that acts on an item, and the plugin carries no code that could carry one
out if it did. The absence is the design.


THE ONE PORT IT OPENS
---------------------

ValeLoot runs a small HTTP server so that F8 can show you an editor with your live bag in it. This is
the whole truth about it, in one place, because it is the thing you would most want told.

* It listens on 127.0.0.1:38512 and nothing else. Not 0.0.0.0, not your LAN address, not your Wi-Fi.
  127.0.0.1 is your own machine talking to itself; a request from anywhere else cannot arrive, and one
  that tries is refused before it is read.
* It serves four things: ValeLoot's editor page (which is compiled into the DLL, not a file on disk),
  a snapshot of YOUR rules, YOUR bag and the game's item list, a save endpoint that writes YOUR rule
  file, and a one-word health check so the page can tell it is being served.
* It carries no game traffic. It is not attached to the game's connection in any way, sees no game
  packet, and contains no packet capture. That distinction is the point: it is a local web page for a
  text file, not a window onto the game's network.
* Nothing leaves your machine. The mod makes no outbound request of any kind - not to check for
  updates, not to report an error, not to anywhere. Play with your network cable out; nothing changes.
* You can turn it off with one line. In BepInEx/config/com.savi.valeloot.cfg:

      [Editor]
      Enabled = false

  Then no port is opened at all, every other feature works exactly as before, and F8 opens the copy of
  the editor that the mod writes to BepInEx/config/ValeLoot-editor.html as a plain local file.

* It says all of this in your log, every launch, so you never have to remember it:

      [Info   :  ValeLoot] editor server on http://127.0.0.1:38512/ - bound to 127.0.0.1 ONLY, so it
                           is reachable from this machine and from nothing else. It serves ValeLoot's
                           own editor page and your own rule file, carries no game traffic, contains
                           no packet capture, and sends nothing anywhere. Turn it off with
                           Enabled = false under [Editor] in com.savi.valeloot.cfg.

AN OLDER VERSION OF THIS README SAID VALELOOT "OPENS NO SOCKETS" AND HAD "NO NETWORKING AT ALL". That
was true of that version and it is false of this one. It is corrected here rather than quietly
reworded, because the value of a claim like that is entirely in whether it is maintained when it stops
being convenient.

If the port is already taken - a second copy of the game, or anything else on 38512 - ValeLoot says so
in one line, keeps every other feature working, and F8 falls back to the local file. Change Port under
[Editor] if you would rather have the server.


THE DEVELOPER'S STANCE, QUOTED EXACTLY
--------------------------------------

Game staff, replying publicly about a loot-presentation tool:

    "...a tool that basically modify how the loot color is, sound and how it looks in the
    inventory. This is allowed but as I said we are not responsible for the applications players
    install and use. It's at your own risk and you are responsible of your own account"

Allowed is not endorsed. Nobody has approved this mod, nobody is going to, and if something goes
wrong with your account that is on you. The same warning is printed in the log every time the game
starts with ValeLoot installed.


INSTALL
-------

Unzip, launch, press F8. There are two downloads and the first one is almost certainly the one you
want.

ValeLoot-0.1.0-with-BepInEx.zip - TAKE THIS ONE

  1. Unzip it into your SpiritVale folder - the folder holding SpiritVale.exe.
  2. Start the game. THE FIRST START TAKES A FEW MINUTES - see below.
  3. Press F8.

THE FIRST LAUNCH IS SLOW, AND THAT IS NORMAL

Before any mod can read the game, BepInEx unpacks it once, writing a few hundred files into
BepInEx/interop/. That takes a few minutes the first time you start the game after installing. The
window may sit black, or look hung, or Windows may grey it out and say "not responding". Leave it
alone. It happens once; every later launch is normal speed.

This is the likeliest reason to believe ValeLoot is broken when it is not, because closing the game
during that first unpack leaves nothing loaded and looks exactly like a failed install. If you want
reassurance while you wait: BepInEx/LogOutput.log keeps growing, and BepInEx/interop/ fills with files.

That is the whole install. The zip carries BepInEx 6 IL2CPP with it, so there is nothing to install
first and no chance of installing the wrong one - which is the commonest way to fail at installing
this mod. BepInEx 5 and the STABLE BepInEx 6 release both look correct and neither loads plugins into
an IL2CPP game, so a player who picks either gets a mod that never runs and no error saying why.

BepInEx is included UNMODIFIED, under its own LGPL-2.1 licence. Its licence text and a link to its
source are in NOTICE.txt at the root of the zip, alongside BepInEx-LICENSE.txt. ValeLoot's own licence
is at the bottom of this file and covers only ValeLoot.

ValeLoot-0.1.0.zip - THE PLUGIN ON ITS OWN

For someone who already runs BepInEx 6 IL2CPP (the BLEEDING-EDGE "be" build). It contains the DLL and
this README and nothing else. Unzip it into the same game folder - the paths inside are already
BepInEx/plugins/ValeLoot/, so nothing needs moving - then start the game and press F8.

The first launch after BepInEx is installed is slow - several minutes, with what looks like a hung
window. That is Il2CppInterop generating proxy assemblies for the whole game, and it happens once.
Let it finish.

There is no HTML file to find and no folder to grant. The editor lives inside the DLL and F8 serves it
to your browser; the mod also drops a copy at BepInEx/config/ValeLoot-editor.html for the times you
want to edit a filter with the game shut. If F8 collides with something else, the log prints the
address at every launch and you can paste it - or set a different key under [Editor].

Checking it worked
~~~~~~~~~~~~~~~~~~

The log is BepInEx/LogOutput.log in your game folder. A healthy boot looks like this:

    [Warning:  ValeLoot] ValeLoot is a client-side loot-presentation mod. Not endorsed by the
                         developer; use at your own risk - you are responsible for your own account.
    [Info   :  ValeLoot] filter loaded: 2 rule(s), 0 always-show, 0 always-hide, 0 with sound, 0 error(s)
    [Info   :  ValeLoot] sounds ready in ...\BepInEx\config\valeloot-sounds
    [Info   :  ValeLoot] census ok  UIInventoryItem (34 methods)
    [Info   :  ValeLoot] census ok  UIInventoryTab.InventoryItemsUID (uid -> cell)
    [Info   :  ValeLoot] census ok  UIInventoryItem.Highlight (cell overlay)
    [Info   :  ValeLoot] census ok  StatData.Value (substat roll %)
    [Info   :  ValeLoot] census ok  App.ServerRuntime (the game's own config database)
    [Info   :  ValeLoot] census ok  Formula.GetSubstatRange (the base cap behind a roll)
    [Info   :  ValeLoot] census ok  PlayerSave.Update (editor tick)
    [Info   :  ValeLoot] census ok  PlayerSave.<Data> (your live character) (offset 0x160)
    [Info   :  ValeLoot] census ok  CharacterData.<Inventory> (the bag behind it) (offset 0xf0)
    [Info   :  ValeLoot] census ok  InventoryData.<Equips> (uid -> equipment) (offset 0x10)
    [Info   :  ValeLoot] census ok  InventoryData.<Artifacts> (uid -> artifact) (offset 0x18)
    [Info   :  ValeLoot] census ok  Input.GetKeyDown(KeyCode) (the editor hotkey)
    [Info   :  ValeLoot] census ok  KeyCode members (322)
    [Info   :  ValeLoot] item reader ready (Data 0x70, Name 0x28, Type 0x38, 24 stat names, rolls readable)
    [Info   :  ValeLoot] item catalog bound, waiting for the client to load configs
                         (App.ServerRuntime 0x18, Equips 0x20, ...)
    [Info   :  ValeLoot] inventory paint ready: 1 RenderPage + 1 Redraw body/bodies hooked
    [Info   :  ValeLoot] tooltip inject ready (OnPointerEnter + TMP_Text.set_text; Data 0x70,
                         UID 0x20, floor 400 chars)
    [Info   :  ValeLoot] pickup watch ready (PlayerSave.Data 0x160, CharacterData.Inventory 0xf0,
                         InventoryData.Equips 0x10, InventoryData.Artifacts 0x18,
                         InventoryData.Cards 0x20, InventoryData.Gems 0x28,
                         InventoryData.Consumables 0x38, InventoryData.Cosmetics 0x40,
                         InventoryData.Junks 0x30, CharacterData.UID 0x20) - loot sounds fire
                         when an item lands in your bag, open or closed, checked 4x a second off the
                         frame tick.
    [Info   :  ValeLoot] editor fallback page written to ...\BepInEx\config\ValeLoot-editor.html -
                         open that file directly if the server is off or its port is busy. It edits
                         the same filter file.
    [Info   :  ValeLoot] editor server on http://127.0.0.1:38512/ - bound to 127.0.0.1 ONLY, so it is
                         reachable from this machine and from nothing else. It serves ValeLoot's own
                         editor page and your own rule file, carries no game traffic, contains no
                         packet capture, and sends nothing anywhere. Turn it off with Enabled = false
                         under [Editor] in com.savi.valeloot.cfg.

and then, once you are logged in and the game has loaded its data:

    [Info   :  ValeLoot] catalog ready: 647 equips, 327 cards, 129 gems, 31 consumables, 280 junk
    [Info   :  ValeLoot] wrote valeloot-items.txt (1414 items) to ...\BepInEx\config\valeloot-items.txt

and, once you are in the world:

    [Info   :  ValeLoot] editor hotkey F8 (KeyCode 289) opens http://127.0.0.1:38512/ in your default
                         browser. It arms once you are in the world; that address works at any time if
                         you would rather paste it.

and, once you have opened your bag:

    [Info   :  ValeLoot] wrote valeloot-bag.txt (137 item(s)) to ...\BepInEx\config\valeloot-bag.txt

The exact numbers vary by game build - the mod resolves everything by name at startup, so the offsets
it prints are whatever today's build uses. What matters is the shape:

* Every census line says "ok", not "MISS". A MISS means the game patch renamed something, and the mod
  tells you loudly at boot rather than quietly painting nothing.
* "inventory paint ready" appeared. If it says "NOT ready", nothing will be highlighted.
* "filter loaded" reports 0 error(s). Every error is printed on its own line with the line number in
  your filter file.
* "catalog ready: N equips, N cards, ..." appeared after you logged in. It cannot happen at startup, because the
  game has not loaded its data yet. Until it does, item names fall back to the text on the cell and
  "Stat Agi >= 3" rules match nothing. If you see "catalog unavailable: ..." instead, that line names
  the reason, and it is printed once rather than on every redraw.
* "editor server on http://127.0.0.1:38512/" appeared. If it says "could NOT bind", that line names
  the port and the reason; nothing else is affected and F8 opens the local copy instead.

If the console fills with "Spawned NetworkObject was expected to exist but does not for Id N",
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

that is the GAME, not ValeLoot. It is SpiritVale's netcode saying a packet named an object your
client cannot see - a unit near you casting at a target outside your visibility - and it repeats for
as long as that goes on, sometimes hundreds of identical lines a second while you stand still at a
storage NPC or anywhere busy. Vanilla logs every one of them too; you never saw them because they
only went to AppData\LocalLow\Baikun\SpiritVale\Player.log, where they still go, with full stack
traces. ValeLoot appears in none of those stacks and hooks nothing on the game's network path.

The with-BepInEx download ships BepInEx/config/BepInEx.cfg with UnityLogListening = false, which
keeps the game's Unity log out of BepInEx's console - where every line is a synchronous write and
therefore costs frames. If you brought your own BepInEx, set that one line under [Logging] yourself.
ValeLoot's own log lines do not come from Unity's log and are unaffected either way.


THE EDITOR
----------

PRESS F8 IN GAME. Your default browser opens on http://127.0.0.1:38512/ and the editor is already
showing your rules, your bag and every item name the game knows. Nothing to find, nothing to point at
a folder, no dialog. The address is in your log at every launch, so if F8 is taken by something else
you can paste it - or set a different key under [Editor] in BepInEx/config/com.savi.valeloot.cfg.

Save writes your filter file directly. One click, no dialog, no downloads folder. The editor tells you
the byte count it wrote, and if the write fails it shows you the reason the operating system gave
rather than a red cross.

A SAVE TAKES EFFECT ON THE NEXT INVENTORY REDRAW. Save, then scroll your bag or reopen it, and the
colours are your new rules. No restart, no relog, and nothing to click in game.

The editor shows how many items in your bag each rule claims, which is what turns rule writing from
guesswork into editing. Served on F8, those counts come straight out of the running game - the same
items the highlight is painting, updated as you scroll. It also says how much of your bag it has seen:
the game hands the mod one page of cells at a time, so the count starts as the page you looked at and
fills in as you scroll and switch tabs. A count that quietly described twelve items of a
two-hundred-item bag would be worse than no count.

The same editor, without the game running
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

The mod also writes the page to BepInEx/config/ValeLoot-editor.html on first run (and refreshes it
when you upgrade). DOUBLE-CLICK THAT to edit a filter with the game shut, or if you turned the server
off, or if its port was busy - F8 opens it for you in exactly those cases.

That copy is a plain local file with nothing behind it. It knows which mode it is in and says so, and
the difference matters for one thing only: SAVING. Served on F8, save writes the file. Opened as a
file, the page has no server to write through, so it uses the browser's own file access: on CHROME
and EDGE it writes valeloot-filter.txt back to the folder you point it at, and on FIREFOX AND SAFARI,
which do not have that API, it downloads the file and you move it into BepInEx/config yourself. It
tells you which of the three you are getting. It never shows you a Save button that does nothing.

Opened as a file it has no live bag either, so its counts come from valeloot-bag.txt - see GENERATED
FILES below for what is in that and when it appears.

You can keep editing the filter file by hand instead; it is a text file and nothing about it changed.


WRITING RULES
-------------

Your filter is BepInEx/config/valeloot-filter.txt. It is written for you, with a working example, the
first time the mod runs. Save it and the game picks it up on the next inventory redraw.

Rules are tried IN ORDER and the FIRST MATCH WINS, so specific rules go at the top and broad ones at
the bottom. That ordering is the whole trick: you stop thinking about overlap.

    Threshold 90                    # what counts as a "top roll" for TopRolls, in percent

    Show "Triple top roll"
        TopRolls  >= 3
        Color     #f472b6
        Tag       TRIPLE
        Highlight glow
        Sound     chime

    Show "Kunai keepers"
        Name      Kunai
        Stat      Agi >= 90%     # roll quality: top tenth of AGI's range
        Highlight mark

    Show "Enough AGI to bother"
        Stat      Agi >= 5       # no %: the number the game prints on the line
        Highlight dot

    Hide "rolled badly"
        AvgRoll   < 35

    AlwaysShow "Spirit Ward", "Windborne Rune"
    AlwaysHide "Rusty Dagger"

Show lights a cell up. Hide claims an item in order to say nothing about it - that is how you stop a
broad rule below from picking up your vendor fodder.

Conditions (all optional, and all must hold for the block to match):

    Name Kunai              part of the item's name, its catalog id, or the text on the cell.
                            Case-insensitive.
    Name "Buzzing Hive Fragment", "Abyssal Idol"
                            comma-separated means ANY of them - one rule, one colour, one sound
    Type Accessory, Rifle   the item's type. Comma-separated means any of them.
    Type Card, Gem, Consumable, Junk
                            the kinds the game gives no type enum; ValeLoot names them
    Stat Agi >= 90%         that substat line rolled in the top 10% of its range
    Stat Agi >= 3           that substat PRINTS at least +3 on this item
    AnyStat                 one Stat line is enough (default: every Stat line must match)
    TopRolls >= 3           at least three lines at or above Threshold
    AvgRoll < 35            mean roll across the item's lines, as a whole percent
    Refine >= 5             refine level at least this
    OverRoll / NoOverRoll   has a line that rolled past 100% - the chaos over-roll
    Chaos / NoChaos         has a chaos type, or has not
    Favorite / NotFavorite  the game's own favourite flag

Decorations, on Show blocks only:

    Color #4ade80               the cell's colour, and the colour of the hover note
    Tag KEEP                    a short word, shown in the hover note. Up to 12 characters.
    Highlight dot|mark|glow     how loud
    Sound chime                 played when you pick a match up, bag open or closed

Rolls versus printed values - the % is the question
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

These two lines look alike and ask opposite things:

    Stat Agi >= 90%   ROLL QUALITY - where the line landed in that stat's legal range on this item
    Stat Agi >= 3     the NUMBER THE GAME PRINTS on the line

They are not two spellings of one idea. Every substat's range runs from two thirds of its maximum up
to its maximum, so a line that rolled 0% still prints a respectable number: on a stat that caps at 4,
">= 3" is satisfied by nearly every roll and ">= 90%" by nearly none. Getting the % wrong does not
shift your filter slightly; it inverts it.

The printed value needs that item's base cap, which ValeLoot reads from the game's own config
database in memory - see THE ITEM REFERENCE below. That data is not loaded when the plugin starts, so
until "catalog ready" appears in the log, a rule using the bare form MATCHES NOTHING and says so in
the log once. It is never quietly treated as satisfied: that would widen the rule to your whole bag.

"Threshold 90" sets what TopRolls counts as a top roll, in percent. It affects TopRolls and nothing
else - not Stat, not AvgRoll.

SharedStats and Verdict are refused with a message: they ask about the gear you are wearing, which
this build does not read. Unknown and Known are refused too - they ask whether a reference catalog is
missing an item, and ValeLoot reads the game's own, so a miss there would be a bug in the mod rather
than a fact about the item.

Generated files
~~~~~~~~~~~~~~~

Two files in BepInEx/config/ are written BY the mod. BOTH ARE GENERATED. EDITING EITHER DOES NOTHING -
your rules live in valeloot-filter.txt, beside them. Nothing is downloaded to produce either one and
neither is shipped in the zip: both are read out of the running game, which is why they are right on
game builds that did not exist when this mod was written.

valeloot-items.txt - every item and stat name the game knows. Written the first time you log in, and
rewritten whenever the item count changes, so a content patch refreshes it. It lists every equip as
name, type, level requirement, set and id, grouped by type, and then every stat name a Stat line
accepts. It is where the editor's autocomplete comes from, and you can grep it yourself - copy a name
out of it, paste it into a rule:

    Show "the good clip"
        Name      "Vampiric Fang Clip"
        Stat      Agi >= 3
        Highlight glow

valeloot-bag.txt - your bag, as the filter sees it. This is what the editor counts against when it is
opened as a plain file; served on F8 it reads the same items live out of the game instead. Written
THE FIRST TIME YOU OPEN YOUR BAG after logging in, and updated as your bag changes. If it is not there
yet, that is why: log in and open your inventory.

It holds EVERY ITEM VALELOOT HAS SEEN IN YOUR BAG THIS SESSION, one line each, keyed by the game's own
id for that individual item. The game binds one page of cells at a time, so it starts as the page you
looked at and fills in as you scroll and switch tabs - the file says so in its own header, and so does
the editor, because a count that quietly describes twelve items of a two-hundred-item bag is worse
than no count at all. An item you have sold or used since it was seen may still be listed. It is
deleted when the game starts, so it never describes an older session.

Neither file is written on a timer or on every redraw: the item list is rewritten when the item count
changes, and the bag file only when your bag's contents actually differ from what is already in it.
Scrolling your bag does not touch your disk.

When a rule is wrong
~~~~~~~~~~~~~~~~~~~~

A block with any bad line is REJECTED WHOLE, and the error names the line number and the reason. That
is deliberate. Ignoring one bad condition would WIDEN the block it was in - delete "Stat Agi >= 90%"
from a Show block and it claims everything you own - so your bag would light up uniformly and the
filter would look broken rather than misread.


SOUNDS
------

Five built-in tones - blip, chime, ding, alert, thud - are written as ordinary .wav files into
BepInEx/config/valeloot-sounds/ on first run. Overwrite any of them with your own file and the filter
line does not change; your file is never overwritten by an upgrade. Drop in
BepInEx/config/valeloot-sounds/anything.wav and write "Sound anything".

THE FOLDER IS THE LIST. Whatever .wav files are in there are what the editor offers you: drop
poe-insane.wav in and it appears in the sound picker within a couple of seconds, no restart, and the
play button plays that actual file over the same loopback port the editor came from. Names must be
letters, digits, dot, dash and underscore - a file called "my sound.wav" cannot be written in a rule
line, so it is left out of the list rather than offered as a rule that would never fire.

VALELOOT SHIPS NO AUDIO beyond the five synthesised tones, deliberately: bundling somebody else's
sounds means redistributing work this repository cannot license. Packs exist - one doing the rounds
is "Path of Exile-style drops for RuneLite (sounds)":

    https://www.reddit.com/r/2007scape/comments/1mk6dv8/path_of_exilestyle_drops_for_runelite_sounds/

A pack is just .wav files, so it drops straight into valeloot-sounds/. What you may do with one is
between you and whoever made it.

A sound plays when the item is PICKED UP. The mod watches your character's own inventory data on the
game's frame tick, four times a second, so loot that lands while your bag is shut still makes its
noise - and opening, scrolling or paging the panel makes none, because nothing was picked up. Ten
things landing at once is one sound, not ten.

EVERY BAG IS WATCHED - equipment, artifacts, cards, gems, consumables, cosmetics and junk. Nothing is
excluded by kind, because the filter is what decides what deserves a noise: the lure called "Buzzing
Hive Fragment" is a consumable and a boss summon, not a potion. A Show block with no conditions on it
claims your whole bag and will chime at everything, and that is your choice to make.

CARDS, JUNK AND CONSUMABLES STACK, so the bag holds one entry per item with the copies counted on it.
A second copy of a card you already own is still a pickup, and still pings.

The first look at a bag is silent: a bag you already own is not a pickup, so the first observation
after you log in just takes note of what is in there. Switching character does the same, so a second
character's bag does not announce itself. Dropping or selling an item forgets it - loot it again and
it pings again.

Sound is Windows-only (it goes through winmm). Everything else works regardless. Turn it off with
"Enabled = false" under [Sound] in BepInEx/config/com.savi.valeloot.cfg.


SETTINGS
--------

BepInEx/config/com.savi.valeloot.cfg, written on first run with every option documented in place.

  [Highlight] TintCell    true     Colour the cell itself. Off leaves the game's plain highlight;
                                   the hover note still works.
  [Highlight] TintDepth    2       How deep to colour under the cell's highlight object (0-4). Only
                                   touch this if cells stop colouring after a game update.
  [Highlight] HoverNote   true     The tooltip line naming the rule that claimed the item.
  [Sound]     Enabled     true     Sounds on picking up a match.
  [Editor]    Enabled     true     The editor server. false opens no port at all - see THE ONE PORT
                                   IT OPENS above.
  [Editor]    Port       38512     The loopback port. Change it if something else has that one.
  [Editor]    Hotkey       F8      Any Unity KeyCode name - F8, F9, Insert, Backslash, Home.

Every one of these exists so that a piece which breaks on a game build newer than the mod can be
turned off without waiting for a release. A cell tint that lands on the wrong object is worse than no
tint, and a port you did not want is worse than no editor.


UNINSTALL
---------

Delete BepInEx/plugins/ValeLoot/ - that takes the DLL, and the editor with it, since the editor is
inside the DLL. Your filter file, the generated valeloot-items.txt and valeloot-bag.txt, the
ValeLoot-editor.html fallback copy and your sounds stay in BepInEx/config/ unless you delete those
too. Nothing is left anywhere else: no registry keys, no AppData, no service, and no firewall rule -
the editor's port is loopback, which needs none.


LICENCE
-------

PolyForm Noncommercial License 1.0.0 - the same licence as the repository this comes from. Source
available, noncommercial use free, commercial use requires a separate written licence.

    Required Notice: Copyright (c) 2026 spiritvalers.com (https://spiritvalers.com)

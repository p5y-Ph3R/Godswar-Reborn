-- Activity window — client-side static lists with server-time day filtering
-- Keeps original behavior; adds offline weekly/daily population and sorting.

-- Notes on time:
--  • We prefer server time (same for all players) if an API is available.
--  • Fallback is Philippines time (UTC+7) so the UI never shows empty lists.

----------------------------------------------------------------
-- UI handles
----------------------------------------------------------------
local uiapi = UIAPI
local win, container, day_list, pro_list, week_list, guide_list, multi_edit = nil, nil, nil, nil, nil, nil, nil
local curTime_text = nil

-- lists' in-memory mirrors
local daylist_cofig, prolist_config, weeklist_cofig, guidelist_cofig, curlist_config = {}, {}, {}, {}, {}

-- dynamic indices
local dcur_index, pcur_index, wcur_index, gcur_index = -1, -1, -1, -1
local hyper_text = ""
local flags, m_index = -1, -1

-- event button flash (kept as in stock file)
local event_btn, btn_flash_visible, btn_a, btn_r, btn_g, btn_b, count, not_click =
  nil, false, 0, 0, 0, 0, 0, true

----------------------------------------------------------------
-- Server time helpers (shared-time across all players)
----------------------------------------------------------------

-- Fallback offset: Philippines time (UTC+7)
local SERVER_UTC_OFFSET_SECONDS = 7 * 3600
-- Try to get a server epoch if the client exposes one.
-- We support several plausible names; if none exist, return nil.
local function try_get_server_epoch()
  local ok, val

  -- 1) GameAPI:GetServerTime() -> epoch seconds (preferred if exists)
  ok, val = pcall(function() return GameAPI and GameAPI.GetServerTime and GameAPI:GetServerTime() end)
  if ok and type(val) == "number" and val > 0 then return val end

  -- 2) GameAPI:GetServerUnixTime() -> epoch seconds
  ok, val = pcall(function() return GameAPI and GameAPI.GetServerUnixTime and GameAPI:GetServerUnixTime() end)
  if ok and type(val) == "number" and val > 0 then return val end

  -- 3) TimeAPI:GetServerTime() -> epoch seconds
  ok, val = pcall(function() return TimeAPI and TimeAPI.GetServerTime and TimeAPI:GetServerTime() end)
  if ok and type(val) == "number" and val > 0 then return val end

  return nil
end

-- Current timestamp in *server* time (epoch seconds).
local function server_now_epoch()
  local server_epoch = try_get_server_epoch()
  if server_epoch then return server_epoch end
  -- Fallback: UTC now + UTC+7 offset to approximate Philippines time
  local utc_now = os.time(os.date("!*t"))
  return utc_now + SERVER_UTC_OFFSET_SECONDS
end

-- Weekday in server time as 1..7 (Mon..Sun)
local function server_weekday()
  local t = os.date("*t", server_now_epoch())
  -- Lua's t.wday: 1=Sun..7=Sat; map to 1=Mon..7=Sun
  local map = { [1]=7,[2]=1,[3]=2,[4]=3,[5]=4,[6]=5,[7]=6 }
  return map[t.wday]
end

-- Optional: if you render server time somewhere else (e.g., MiniMap),
-- you may call its function to keep visuals consistent. Reference:
-- MinMapSetServerTime(str) from MiniMapProc.lua (sets a time label text). :contentReference[oaicite:0]{index=0}

----------------------------------------------------------------
-- Static lists
-- Type: 2 = Important Announcement, 3 = Weekly (also eligible for Today filtering)
-- Days array uses 1=Mon … 7=Sun
----------------------------------------------------------------
local STATIC_EVENTS = {
  -- Important Announcements (kept separate; no day filtering)
  
  {
    Type = 2,
    Name = "Godswar Online Origin — Official Launch",
    Link = "",
    Content =
      "We are excited to finally release the official version of our server on October 31, 2025.\n" ..
      "Thank you to all Alpha and Beta testers who helped us get here.\n" ..
      "We hope everybody enjoys the server!",
  },

  {
    Type = 2,
    Name = "Join Our Discord",
    Link = "https://discord.gg/mJmwgQWa4J",
    Content =
      "Stay up to date with announcements, patch notes, events, and community support.\n\n" ..
      "Invite: https://discord.gg/mJmwgQWa4J\n\n" ..
      "Website: https://godswarorigin.net",
  },

    {
    Type = 2,
    Name = "Current Donation Packages",
    Link = "https://godswarorigin.net",  
    Content =
	"We thank you very much for choosing to support our project!\n" .. 
	"Bellow you can see the current donation packages we offer:\n\n" .. 
      "Normal Packages:             $10\n" ..
      "Monthly Limited:             $50\n" ..
      "Monthly Donation Limit:     $150\n" ..
      "Gold Rate:            $1 = 1000\n" ..
      "B-Gold Rate:          $1 = 3000\n\n" ..
      "Pet Package ($10)\n" ..
      "• 1 Phoenix's Feather\n" ..
      "• 10 Fairy Feather\n" ..
      "• 2 Crystal Gift Pack (mjades randomized) or 2 Crystal Gift Pack (Pet skill randomized)\n\n" ..
      "Gear Package ($10)\n" ..
      "• 2 Level 4 Sapphire\n" ..
      "• 2 Level 4 Emerald\n" ..
      "• 2 Gear Improvement Package I\n" ..
      "• Big Money Bag\n\n" ..
      "Skillbook Package ($10)\n" ..
      "• 5 Zeus Blessing\n" ..
      "• 99 Talent Stones 4\n" ..
      "• Big Money Bag\n\n" ..
      "Grinding Package ($10)\n" ..
      "• 6 EXP Potion (300%)\n" ..
      "• 6 Talent Potion (300%)\n" ..
      "• 12 Pet EXP Potion (300%)\n" ..
      "• Big Money Bag\n\n" ..
      "PvP Package ($10)\n" ..
      "• 99 Magic Healing Potion\n" ..
      "• 99 Magic Mana Potion\n" ..
      "• 25 Hercules Fruit\n" ..
      "• 10 Anima Potion\n" ..
      "• 10 Energy Holy Water\n" ..
      "• Big Money Bag\n\n" ..
      "Monthly Limited ($50)\n" ..
      "• Level 80 Mount (except AAD)\n" ..
      "• VIP Suit (30 Days) or Bruce Lee (30 Days)\n" ..
      "• 1 Spring Water\n" ..
      "• 5 Phoenix Feather\n" ..
      "• Smart Pet Egg\n" ..
      "• 3 Crystal Gift Pack (mjades randomized)\n" ..
      "• 3 Crystal Gift Pack (Pet skill randomized)\n" ..
      "• Greater PvP Kit\n" ..
      "• Big Money Bag",
  },
  ----------------------------------------------------------------
  -- Weekly events (10 total). Each has Days=... and shared Content.
  -- These appear on the Weekly tab always (sorted by Name),
  -- AND on the Today tab when today ∈ Days.
  ----------------------------------------------------------------

  {
	Type = 3,
	Name = "Spy Invasion",
	Time = "Daily",
	Days = {1,2,3,4,5,6,7},
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"During this event, hostile spies appear in your faction capital (Athens or Sparta).\n" ..
		"Defeat them to earn experience and talent stones.\n\n" ..
		"Active days: Mon–Sun (daily)\n" ..
		"Active windows (server time): 12:35–12:45 and 20:35–20:45\n\n" ..
		"Rewards:\n" ..
		"• Experience Stone 1: 2,000 XP\n" ..
		"• Experience Stone 2: 5,000 XP\n" ..
		"• Experience Stone 3: 10,000 XP\n" ..
		"• Talent Stone 1: 5 talent points\n" ..
		"• Talent Stone 2: 10 talent points\n" ..
		"• Talent Stone 3: 20 talent points",
	},

  {
	Type = 3,
	Name = "Ni Mini Valley",
	Time = "Fri/Sat/Sun",
	Days = {5,6,7},  -- Fri, Sat, Sun
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Duration: 45 minutes.\n" ..
		"Opening hours: Fri 19:00, Sat 07:00, Sun 19:00.\n" ..
		"Eligibility: Levels 70–89.\n\n" ..
		"Rules:\n" ..
		"• Each enemy kill = +1 personal score and +1 faction score.\n\n" ..
		"Rewards:\n" ..
		"• Claim prizes at the Battlefield Awarder NPC.\n" ..
		"• Top killer per faction earns a weekly title: Hope of Sparta / Hope of Athens.",
	AutoName1 = "Battlefield Awarder", AutoAddr1="0,178,-38,1,178,-38",
	},

  {
	Type = 3,
	Name = "War Materials",
	Time = "All Day",
	Days = {1,2,3,4,5,6,7}, -- Daily
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Players level 20+ who are members of a guild can participate.\n\n" ..
		"How it works:\n" ..
		"• Guild leader or deputy starts the quest at the [Guild] Quest Supervisor NPC.\n" ..
		"• Once activated, all guild members can see it in the Quest Journal.\n" ..
		"• Donate materials to Military Supplier 3 in Marathon (-107, -176) or Peloponnese (-66, -81).\n\n" ..
		"Limits:\n" ..
		"• Up to 3 material delivery quests per player per day.\n" ..
		"• No more than 6 deliveries can be made per day (cap).\n\n" ..
		"Rewards:\n" ..
		"• Guild Contribution Points, Silver, Experience, Talent Points.\n\n" ..
		"Notes:\n" ..
		"• Outside of the event, all war materials can be purchased using Gold or B-Gold.\n" ..
		"• Active days: Mon–Sun (daily).",
	AutoName1 = "[Guild] Quest Supervisor NPC", AutoAddr1="0,55,-98,1,55,-98",
	},

  {
	Type = 3,
	Name = "Treasure Land",
	Time = "Sun",
	Days = {7}, 
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Duration: 30 minutes.\n" ..
		"Opening hours: Sunday 09:00-09:30 and 22:00-22:30.\n\n" ..
		"How to join:\n" ..
		"• Speak with the Event Transporter NPC\n" .. 
		"to enter the event area.\n\n" ..
		"Rules & flow:\n" ..
		"• Upon entry, you automatically receive 40 Wooden Nets.\n" ..
		"• Monsters are scattered across the map; search beneath the map to find your level range.\n" ..
		"• Do NOT attack monsters—catch them with nets.\n" ..
		"• Experienced players with enough HP can try catching bosses at the map center.\n\n" ..
		"Rewards:\n" ..
		"• Higher-level catches yield better rewards.\n\n" ..
		"Active days: Sunday only.",
	AutoName1 = "Event Transporter", AutoAddr1="0,143,-39,1,143,-39",
	},

  {
	Type = 3,
	Name = "Lelantine Farm",
	Time = "Sat",
	Days = {6}, 
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Players level 31+ can enter via the Battlefield Transporter NPC.\n" ..
		"Duration: 30 minutes. PvP is enabled in specific areas." ..
		"Opening hours : Saturday 09:00-09:30 and 21:00-21:30.\n\n" ..
		"Rules & scoring:\n" ..
		"• Most points wins; a draw is possible.\n" ..
		"• Regular monster: 1 point.\n" ..
		"• Elite monster: 10 points.\n" ..
		"• Final boss Cerberus (in its lair): 1,000 faction points;\n\n" ..
		"Dog catching & rewards:\n" ..
		"• Catching the dog yields a Faithful Dog Egg.\n" ..
		"• Exchange eggs with your faction's Advance Troop Captain for points and pet EXP (higher-rank eggs give better rewards).\n\n" ..
		"Additional quest:\n" ..
		"• When the battlefield opens, Delighted Kelsis (farm owner) buys puppies matching requested traits for extra rewards.\n\n" ..
		"Awards:\n" ..
		"• After the battle, claim rewards from the Battlelefield Awarder NPC.\n\n" ..
		"Active day: Saturday",
	AutoName1 = "Battlefield Transporter", AutoAddr1="0,174,-40,1,174,-40",
	},

  {
	Type = 3,
	Name = "Pindus Battlefield",
	Time = "Wed/Fri/Sat/Sun",
	Days = {3,5,6,7}, 
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Join via the Battlefield Transporter NPC.\n" ..
		"Duration: 45 minutes. Levels 31–120 from both factions.\n" ..
		"Opening Hours: Wed 21:00; Fri 21:00; Sat 08:00/15:00; Sun 08:00/21:00\n\n" ..
		"Scoring & objectives:\n" ..
		"• Earn faction points and personal contribution by killing bosses, defeating hostile players, or trading a special item.\n" ..
		"• When time ends, the faction with the most points wins.\n\n" ..
		"Rewards:\n" ..
		"• All participants gain EXP, Talent, B-Gold, and Reputation (winners receive more).\n" ..
		"• Top five players by score earn a title and extra rewards.\n\n" ..
		"Active days: Wed, Fri, Sat, Sun.",
	AutoName1 = "Battlefield Transporter", AutoAddr1="0,174,-40,1,174,-40",
	},

  {
	Type = 3,
	Name = "Cursed Zone",
	Time = "All Day",
	Days = {1,2,3,4,5,6,7}, -- Daily
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Duration: All day\n" ..
		"Objective:\n" ..
		"• Hunt monsters across Cursed Zones.\n\n" ..
		"Rewards:\n" ..
		"• Monsters drop Dusts that can be combined into special Attribute Stones.\n\n",
	},

  {
	Type = 3,
	Name = "Fitness Racing",
	Time = "Mon–Sat",
	Days = {1,2,3,4,5,6}, -- Monday to Saturday
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"All-day racing available Monday through Saturday (not on Sundays).\n" ..
		"Participation limit: Once per day.\n\n" ..
		"How to start:\n" ..
		"• Speak with Fitness Trainer 1\n" .. 
		"in the Athens/Sparta suburbs, then collect the required Health Manuals.\n\n" ..
		"Rewards:\n" ..
		"• 3,600 B-Gold upon completion.\n\n" ..
		"• Random rewards for each Health Manual handed in.\n\n" ..
		"Active days: Mon–Sat",
	AutoName1 = "Fitness Trainer 1", AutoAddr1 = "",
	},

  {
	Type = 3,
	Name = "Gift of Zeus",
	Time = "Sat",
	Days = {6}, 
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Starts at Saturday 12:00 and runs until midnight (kickoff window). After kickoff, players can make DAILY offerings all week.\n\n" ..
		"Where to start:\n" ..
		"• Speak with Zeus' Loyal Believer\n"..
		"in Sparta/Athens (hint location ~155, -62) to learn which gifts are currently desired.\n" ..
		"• Requested items may differ between players.\n\n" ..
		"Offerings & wishes:\n" ..
		"• Each offering grants a Prayer Stone plus EXP and Talent Points.\n" ..
		"• Players begin with 5 free wishes.\n" ..
		"• Every 5 Prayer Stones deposited during the week grants +1 additional wish attempt.\n" ..
		"• Each wish costs 20 Dusts.\n" ..
		"• Weekday offering limit: up to 10 times per day.\n" ..
		"• Weekend offering limit: up to 12 times per day.\n\n" ..
		"Limited rewards (server-wide):\n" ..
		"• Chance to obtain Spring Waters, Fairy Feathers, B-Gold sacks, and more—first come, first served.\n\n" ..
		"Luck Matters side event:\n" ..
		"• Each Prayer Stone deposited grants 1 chance to enter.\n" ..
		"• Pay 10 Dusts to roll a number (0–1000) for a specific item.\n" ..
		"• The highest roll by Sunday 12:00 wins the reward.\n\n" ..
		"Active days: \n" ..
		"• Praying Stones Deposit: Daily \n" ..
		"• Zues' Gift Event: Sat 12:00 \n" ..
		"• Luck Matters: Sat 12:00 - Sun 12:00",
	AutoName1 = "Zeus' Loyal Believer", AutoAddr1="0,155,-62,1,155,-62",
	},

	{
	Type = 3,
	Name = "The Lost Book",
	Time = "All day",
	Days = {1,2,3,4,5,6,7}, 
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Daily event across both factions' suburbs.\n\n" ..
		"How to play:\n" ..
		"• Hunt Evil Boogymen in the suburbs to collect Sheepskin Scrolls.\n" ..
		"• Exchange Scrolls for Sheepskin Books at the appropriate Totems across various maps.\n" ..
		"• Turn in required Books to the Mysterious Elder\n" .. 
		"in your capital to receive an Incomplete or Complete Book.\n" ..
		"• Exchange the Book at the Wishing Pool for significant rewards.\n\n" ..
		"Active days: Mon–Sun (Daily)",
	AutoName1 = "Mysterious Elder", AutoAddr1 = "0,76,-134,1,76,-134",
	},

	{
	Type = 3,
	Name = "Labyrinth of Hera",
	Time = "Daily",
	Days = {1,2,3,4,5,6,7}, -- Daily
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Location: \n • Athens → Cyclades;\n• Sparta → Dodecanese.\n" ..
		"Eligibility: Level 20+.\n\n" ..
		"How to start:\n" ..
		"• Speak with the Labyrinth Guide NPC to accept the quest.\n" ..
		"• Enter via the Event Transporter NPC\n" .. 
		"(destination: “Cyclades Islands / Dodecanese”).\n\n" ..
		"Mechanics:\n" ..
		"• The Labyrinth has 9 islands; each island has 4 teleporters to other islands.\n" ..
		"• Talk to the Servant of Zeus on the first island to receive “Oracle of Zeus 1”.\n" ..
		"• Find the five Messengers of Zeus across five different islands; hand in each Oracle in order to receive the next (up to Oracle of Zeus 5).\n\n" ..
		"Rewards:\n" ..
		"• Experience scaled to player level, Talent Points, and Zodiac Energy.\n\n" ..
		"Active days: Mon–Sun (Daily).",
		AutoName1 = "Event Transporter", AutoAddr1="0,143,-39,1,143,-39",
	},

  {
	Type = 3,
	Name = "Romance Island",
	Time = "Thu/Sat/Sun",
	Days = {3,6,7},  -- Wed, Sat, Sun
    Link = "https://discord.gg/mJmwgQWa4J",
	Content =
		"Duration: 30 minutes.\n" ..
    "Opening hours: Wed 20:00, Sat 20:00, Sun 16:30.\n" ..
    "Eligibility: Levels 70–140.\n\n" ..
		"Objective:\n" ..
		"• Gather Crafting Materials with the designated tools.\n\n" ..
		"Rewards:\n" ..
		"• Crafting Materials.\n\n",
	},

}

----------------------------------------------------------------
-- Utility: add items to UI lists (mirrors the stock behavior)
----------------------------------------------------------------

local function add_to_daylist(ev)
  dcur_index = dcur_index + 1
  daylist_cofig[dcur_index] = ev
  local item = uiapi:AddItem("T_ListItem", day_list)
  local t  = uiapi:AddText("T_ListItemText",  item)
  local t1 = uiapi:AddText("T_ListItemText1", item)
  t:SetText(ev.Time or "")
  t1:SetText(ev.Name or "")
end

local function add_to_prolist(ev)
  pcur_index = pcur_index + 1
  prolist_config[pcur_index] = ev
  local item = uiapi:AddItem("T_ListItem", pro_list)
  local t1 = uiapi:AddText("T_ListItemText2", item)
  t1:SetText(ev.Name or "")
end

local function add_to_weeklist(ev)
  wcur_index = wcur_index + 1
  weeklist_cofig[wcur_index] = ev
  local item = uiapi:AddItem("T_ListItem", week_list)
  local t  = uiapi:AddText("T_ListItemText",  item)
  local t1 = uiapi:AddText("T_ListItemText1", item)
  t:SetText(ev.Time or "")
  t1:SetText(ev.Name or "")
end

local function add_to_guidelist(ev)
  gcur_index = gcur_index + 1
  guidelist_cofig[gcur_index] = ev
  local item = uiapi:AddItem("T_ListItem", guide_list)
  local t  = uiapi:AddText("T_ListItemText",  item)
  local t1 = uiapi:AddText("T_ListItemText1", item)
  t:SetText(ev.Time or "")
  t1:SetText(ev.Name or "")
end

local function clear_all_lists()
  uiapi:ClearItem(day_list);  uiapi:ClearItem(pro_list)
  uiapi:ClearItem(week_list); uiapi:ClearItem(guide_list)
  daylist_cofig, prolist_config, weeklist_cofig, guidelist_cofig, curlist_config = {},{},{},{},{}
  dcur_index, pcur_index, wcur_index, gcur_index = -1, -1, -1, -1
  hyper_text, flags, m_index = "", -1, -1
  multi_edit:SetText("")
end

local function event_is_today(ev)
  if type(ev.Days) ~= "table" then return false end
  local today = server_weekday()
  for _,d in ipairs(ev.Days) do if d == today then return true end end
  return false
end

----------------------------------------------------------------
-- Populate lists from static data (and keep server updates compatible)
----------------------------------------------------------------
-- Count how many distinct days an event covers (Daily => 7)
local function day_count(ev)
  if ev.Daily then return 7 end
  if type(ev.Days) == "table" then
    local seen, c = {}, 0
    for _, d in ipairs(ev.Days) do
      if not seen[d] then seen[d] = true; c = c + 1 end
    end
    return c
  end
  return 0
end
local function LoadStaticEvents()
  clear_all_lists()

  -- Buffers (we'll sort before rendering)
  local weekly_buf = {}
  local today_buf  = {}

  for _, ev in ipairs(STATIC_EVENTS) do
    -- normalize optional fields
    ev.Time      = ev.Time or ""
    ev.Name      = ev.Name or ""
    ev.Link      = ev.Link or ""
    ev.Content   = ev.Content or ""
    ev.AutoName1 = ev.AutoName1 or ""; ev.AutoAddr1 = ev.AutoAddr1 or ""
    ev.AutoName2 = ev.AutoName2 or ""; ev.AutoAddr2 = ev.AutoAddr2 or ""
    ev.AutoName3 = ev.AutoName3 or ""; ev.AutoAddr3 = ev.AutoAddr3 or ""
    ev.AutoName4 = ev.AutoName4 or ""; ev.AutoAddr4 = ev.AutoAddr4 or ""

    if ev.Type == 2 then
      add_to_prolist(ev) -- Important Announcements (separate)

    elseif ev.Type == 3 then
      -- Weekly is always shown (sorted later)
      table.insert(weekly_buf, ev)
      -- If active today (server time), also buffer for Today list
      if event_is_today(ev) then table.insert(today_buf, ev) end

    elseif ev.Type == 4 then
      add_to_guidelist(ev) -- compatibility, unused now
    end
  end

  -- Sort Weekly A→Z
  table.sort(weekly_buf, function(a,b)
    return (a.Name or ""):lower() < (b.Name or ""):lower()
  end)
  for _, ev in ipairs(weekly_buf) do add_to_weeklist(ev) end

  -- Sort Today by coverage (more days first), then A→Z
  table.sort(today_buf, function(a,b)
    local ca, cb = day_count(a), day_count(b)
    if ca ~= cb then return ca > cb end
    return (a.Name or ""):lower() < (b.Name or ""):lower()
  end)
  for _, ev in ipairs(today_buf) do add_to_daylist(ev) end

  -- default selection: Pro → Today → Weekly
  if uiapi:GetListCount(pro_list) >= 1 then
    uiapi:ActiveList(0, pro_list);   curlist_config = prolist_config[0]
  elseif uiapi:GetListCount(day_list) >= 1 then
    uiapi:ActiveList(0, day_list);   curlist_config = daylist_cofig[0]
  elseif uiapi:GetListCount(week_list) >= 1 then
    uiapi:ActiveList(0, week_list);  curlist_config = weeklist_cofig[0]
  end

  if curlist_config then
    hyper_text = curlist_config.Link or ""
    multi_edit:SetText(curlist_config.Content or "")
    Event_SetAutoSeekWay()
  end

  container:Visible(true)
  container:Auto()
end

----------------------------------------------------------------
-- Stock hooks + server-push compatibility (unchanged semantics)
----------------------------------------------------------------
function Event_OnLoad()
  win         = uiapi:GetElement(481000); win:SetPosition(101,150)
  day_list    = uiapi:GetElement(481100)
  pro_list    = uiapi:GetElement(481120)
  week_list   = uiapi:GetElement(481140)
  guide_list  = uiapi:GetElement(481160)
  multi_edit  = uiapi:GetElement(481401)
  container   = uiapi:GetElement(481400)
  curTime_text= win:GetChild("CurrentDate")

  event_btn   = uiapi:GetElement(481610)
  btn_r, btn_g, btn_b, btn_a = event_btn:GetFontColor()

  clear_all_lists()
  LoadStaticEvents()

  -- Keep listening for server updates; they’ll overwrite/refresh if they arrive.
  this:RegisterEvent(EUSER_EVENT_UPDATEEVENTINFO , "UpDate_EventInfo()")
end

function Event_Init()
  clear_all_lists()
  LoadStaticEvents()
end

-- GM flags / flashing
function Event_GMFlags(flags_in)
  if flags_in == 1 then
    GMEventInfo_WinVisible()
  elseif flags_in == 2 then
    btn_flash_visible, not_click = true, true
  elseif flags_in == 3 then
    btn_flash_visible, not_click = false, true
  end
end

-- Server-driven incremental update (kept intact to remain compatible)
function UpDate_EventInfo()
  local Flags,Type,Time,Name,Link,Content,AutoName1,AutoAddr1,AutoName2,AutoAddr2,AutoName3,AutoAddr3,AutoName4,AutoAddr4 =
    GameAPI:GetEventInfo()

  if Name == "" then return end

  if Type == 1 then
    if Flags == 1 or Flags == 3 then
      multi_edit:SetText(""); hyper_text=""; flags=-1; m_index=-1; dcur_index=-1
      daylist_cofig = {}; uiapi:ClearItem(day_list)
    end
    dcur_index = dcur_index + 1
    daylist_cofig[dcur_index] = { Time=Time, Name=Name, Link=Link, Content=Content,
      AutoName1=AutoName1, AutoAddr1=AutoAddr1, AutoName2=AutoName2, AutoAddr2=AutoAddr2,
      AutoName3=AutoName3, AutoAddr3=AutoAddr3, AutoName4=AutoName4, AutoAddr4=AutoAddr4 }
    local item = uiapi:AddItem("T_ListItem", day_list)
    local text  = uiapi:AddText("T_ListItemText",  item)
    local text1 = uiapi:AddText("T_ListItemText1", item)
    text:SetText(Time); text1:SetText(Name)

  elseif Type == 2 then
    if Flags == 1 or Flags == 3 then
      multi_edit:SetText(""); hyper_text=""; flags=-1; m_index=-1; pcur_index=-1
      prolist_config = {}; uiapi:ClearItem(pro_list)
    end
    pcur_index = pcur_index + 1
    prolist_config[pcur_index] = { Time=Time, Name=Name, Link=Link, Content=Content,
      AutoName1=AutoName1, AutoAddr1=AutoAddr1, AutoName2=AutoName2, AutoAddr2=AutoAddr2,
      AutoName3=AutoName3, AutoAddr3=AutoAddr3, AutoName4=AutoName4, AutoAddr4=AutoAddr4 }
    local item = uiapi:AddItem("T_ListItem", pro_list)
    local text1 = uiapi:AddText("T_ListItemText2", item)
    text1:SetText(Name)

  elseif Type == 3 then
    if Flags == 1 or Flags == 3 then
      multi_edit:SetText(""); hyper_text=""; flags=-1; m_index=-1; wcur_index=-1
      weeklist_cofig = {}; uiapi:ClearItem(week_list)
    end
    wcur_index = wcur_index + 1
    weeklist_cofig[wcur_index] = { Time=Time, Name=Name, Link=Link, Content=Content,
      AutoName1=AutoName1, AutoAddr1=AutoAddr1, AutoName2=AutoName2, AutoAddr2=AutoAddr2,
      AutoName3=AutoName3, AutoAddr3=AutoAddr3, AutoName4=AutoName4, AutoAddr4=AutoAddr4 }
    local item = uiapi:AddItem("T_ListItem", week_list)
    local text  = uiapi:AddText("T_ListItemText",  item)
    local text1 = uiapi:AddText("T_ListItemText1", item)
    text:SetText(Time); text1:SetText(Name)

  elseif Type == 4 then
    if Flags == 1 or Flags == 3 then
      multi_edit:SetText(""); hyper_text=""; flags=-1; m_index=-1; gcur_index=-1
      guidelist_cofig = {}; uiapi:ClearItem(guide_list)
    end
    gcur_index = gcur_index + 1
    guidelist_cofig[gcur_index] = { Time=Time, Name=Name, Link=Link, Content=Content,
      AutoName1=AutoName1, AutoAddr1=AutoAddr1, AutoName2=AutoName2, AutoAddr2=AutoAddr2,
      AutoName3=AutoName3, AutoAddr3=AutoAddr3, AutoName4=AutoName4, AutoAddr4=AutoAddr4 }
    local item = uiapi:AddItem("T_ListItem", guide_list)
    local text  = uiapi:AddText("T_ListItemText",  item)
    local text1 = uiapi:AddText("T_ListItemText1", item)
    text:SetText(Time); text1:SetText(Name)
  end

  -- Default focus when server pushes anything
  local cnt = uiapi:GetListCount(pro_list)
  if cnt >= 1 then
    uiapi:ActiveList(0, pro_list)
    multi_edit:SetText("")
    hyper_text = prolist_config[0].Link
    multi_edit:SetText(prolist_config[0].Content)
    curlist_config = prolist_config[0]
    Event_SetAutoSeekWay()
    uiapi:ActiveList(-1, day_list); uiapi:ActiveList(-1, week_list); uiapi:ActiveList(-1, guide_list)
    container:Auto()
  end
end

----------------------------------------------------------------
-- Selection handlers (same content on both pages)
----------------------------------------------------------------
function Event_DayList_OnSelected()
  multi_edit:SetText("")
  m_index = uiapi:GetActiveList(day_list)
  curlist_config = daylist_cofig[m_index]
  hyper_text = curlist_config.Link
  multi_edit:SetText(curlist_config.Content)
  Event_SetAutoSeekWay()
  uiapi:ActiveList(-1, pro_list); uiapi:ActiveList(-1, week_list); uiapi:ActiveList(-1, guide_list)
  container:Auto()
end

function Event_ProList_OnSelected()
  multi_edit:SetText("")
  m_index = uiapi:GetActiveList(pro_list)
  curlist_config = prolist_config[m_index]
  hyper_text = curlist_config.Link
  multi_edit:SetText(curlist_config.Content)
  Event_SetAutoSeekWay()
  uiapi:ActiveList(-1, day_list); uiapi:ActiveList(-1, week_list); uiapi:ActiveList(-1, guide_list)
  container:Auto()
end

function Event_WeekList_OnSelected()
  multi_edit:SetText("")
  m_index = uiapi:GetActiveList(week_list)
  curlist_config = weeklist_cofig[m_index]
  hyper_text = curlist_config.Link
  multi_edit:SetText(curlist_config.Content)
  Event_SetAutoSeekWay()
  uiapi:ActiveList(-1, day_list); uiapi:ActiveList(-1, pro_list); uiapi:ActiveList(-1, guide_list)
  container:Auto()
end

function Event_GuideList_OnSelected()
  multi_edit:SetText("")
  m_index = uiapi:GetActiveList(guide_list)
  curlist_config = guidelist_cofig[m_index]
  hyper_text = curlist_config.Link
  multi_edit:SetText(curlist_config.Content)
  Event_SetAutoSeekWay()
  uiapi:ActiveList(-1, day_list); uiapi:ActiveList(-1, pro_list); uiapi:ActiveList(-1, week_list)
  container:Auto()
end

----------------------------------------------------------------
-- Hyperlink & keyword autopath (unchanged)
----------------------------------------------------------------
function Event_OnTextSelect()
  local str = uiapi:GetTextSelect(multi_edit)
  for i = 1, 4, 1 do
    local name = "AutoName"..i
    if curlist_config[name] ~= "" then
      if string.find(str, curlist_config[name]) then
        local addr = ""
        if i == 1 then      addr = curlist_config["AutoAddr1"]
        elseif i == 2 then  addr = curlist_config["AutoAddr2"]
        elseif i == 3 then  addr = curlist_config["AutoAddr3"]
        elseif i == 4 then  addr = curlist_config["AutoAddr4"]
        end
        local a,b,mapid,posx,posy,amapid,aposx,aposy = string.find(addr,"(-?%d+),(-?%d+),(-?%d+),(-?%d+),(-?%d+),(-?%d+)")
        local camp = PlayerAPI:GetGameData():GetFaction()
        if camp == 0 then GameAPI:AutoSeekWay(mapid,posx,posy) else GameAPI:AutoSeekWay(amapid,aposx,aposy) end
        return
      end
    end
  end
end

function Event_SetAutoSeekWay()
  uiapi:ClearTextSelectKey(multi_edit)
  for i = 1, 4, 1 do
    local name = "AutoName"..i
    if curlist_config[name] ~= "" then
      uiapi:SetTextSelectColor(i-1, "HelpSystem_Color1", multi_edit)
      uiapi:SetTextSelectKey(i-1, curlist_config[name], multi_edit)
    end
  end
end

function PlayerLinkBtn_OnClick()
  uiapi:HyperLink(hyper_text)
end

----------------------------------------------------------------
-- Button / flashing / date label (kept)
----------------------------------------------------------------
function EventInfo_OnClick() GMEventInfo_OnClick() end

function EventBtn_OnClick()
  win = uiapi:GetElement("ActivityWin")
  win:Visible(not win:IsVisible())
  if win:IsVisible() then
    win:SetPosition(101,150); win:Top()
    if btn_flash_visible then not_click = false end
  end
end

function EventBtn_Update()
  if btn_flash_visible and not_click then
    count = count + 1
    if count % 30 == 0 then
      this:SetFontColor(255, 0, 0, btn_a)
    elseif count % 15 == 0 then
      this:SetFontColor(0, 255, 0, btn_a)
    end
  else
    this:SetFontColor(btn_r, btn_g, btn_b, btn_a)
    count = 0
  end
end

-- Optional label (if used in your XML)
function Event_Time(text, day)
  -- Set your own localized weekday text if needed.
  curTime_text:SetText(text or "")
end

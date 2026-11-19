-- ============================================================================
-- Berseria Archipelago - in-game runtime
--
-- Injected into the game's Lua VM by the C# plugin (BerseriaAP.dll). It patches
-- the game's own script modules so that:
--   * first-time chest / Katz box / sparkle point pickups become Archipelago checks
--   * received items are added to the inventory
--   * entering a locked region through a map link is refused
--
-- Communication with C#: C# calls BERSERIA_AP.* functions at safe points; this file
-- queues events in AP.out, which C# drains with AP.Poll().
-- The file is idempotent: loading it again keeps state and re-applies patches.
-- ============================================================================

local AP = rawget(_G, "BERSERIA_AP")
if not AP then
  AP = { out = {}, orig = {}, scout = {}, locked = nil, active = false, sparkles = false,
         msgs = {}, lastScenario = -1, lockCooldown = 0 }
  rawset(_G, "BERSERIA_AP", AP)
end
AP.version = 1

local TB      = require("AreaTreasureBox")
local TP      = require("AreaTreasurePoint")
local MapLink = require("AreaMapLink")
local ItemU   = require("TO13ItemUtility")
local SysWin  = require("TO13SystemWindow")
local CBSave  = require("TO13CatBoxSaveData")
local TBSave  = require("TO13TreasureBoxSaveData")

local SENTINEL = "BERSERIA_AP_ITEM"

local function emit(...)
  AP.out[#AP.out + 1] = table.concat({ ... }, "\t")
end

local function keepOrig(name, fn)
  if AP.orig[name] == nil then AP.orig[name] = fn end
  return AP.orig[name]
end

-- ---------------------------------------------------------------------------
-- Treasure boxes: the first opening gives the record's fixed item. Replace it.
-- ---------------------------------------------------------------------------
local function wrapCreate(name)
  local orig = keepOrig("TB." .. name, TB[name])
  TB[name] = function(self, rec)
    if AP.active and self.data and not self.data.first and rec and rec.Id then
      local key = "TB:" .. rec.Id
      emit("CHECK", key)
      return SENTINEL, { count = 1, ap = key }
    end
    return orig(self, rec)
  end
end
wrapCreate("CreateTreasure")
wrapCreate("CreateTreasureExtra")

-- The chest sequence calls TO13ItemUtility.AcquireSequence(item, params) inside a
-- coroutine, so we can show a message window here.
do
  local orig = keepOrig("ItemU.AcquireSequence", ItemU.AcquireSequence)
  ItemU.AcquireSequence = function(item, params)
    if item == SENTINEL then
      local key = params and params.ap
      local text = (key and AP.scout[key]) or "Found an Archipelago item!"
      SysWin.ShowMessage(text)
      return true
    end
    return orig(item, params)
  end
end

-- ---------------------------------------------------------------------------
-- Katz boxes: opening one is a check (vanilla Katz rewards stay untouched).
-- ---------------------------------------------------------------------------
do
  local orig = keepOrig("CBSave.Acquire", CBSave.Acquire)
  CBSave.Acquire = function(rec, ...)
    if AP.active and rec and rec.Id then emit("CHECK", "CB:" .. rec.Id) end
    return orig(rec, ...)
  end
end

-- ---------------------------------------------------------------------------
-- Sparkle points (optional): first pickup of each point is a check.
-- ---------------------------------------------------------------------------
do
  local orig = keepOrig("TP.Acquire", TP.Acquire)
  TP.Acquire = function(self, ...)
    if AP.active and AP.sparkles and self.data and not self.data.discover and self.id then
      local key = "TP:" .. self.id
      emit("CHECK", key)
      -- Mark as picked up exactly like the original does, but skip the vanilla item.
      self.data.discover = true
      if self.AddNaviIcon then pcall(self.AddNaviIcon, self) end
      self.data.random = math.random(1, 65536)
      self.data.lastest = TO13.Scenario.GetLocationChangeCount()
      SysWin.ShowMessage(AP.scout[key] or "Found an Archipelago item!")
      self:Disable()
      return
    end
    return orig(self, ...)
  end
end

-- ---------------------------------------------------------------------------
-- Region keys: refuse map links whose destination region is locked.
-- ---------------------------------------------------------------------------
local function regionOf(map)
  if type(map) ~= "string" or map:sub(1, 4) ~= "MAP_" then return nil end
  return map:sub(5, 7)
end

do
  local orig = keepOrig("MapLink.ExecuteChangeMap", MapLink.ExecuteChangeMap)
  MapLink.ExecuteChangeMap = function(self, ...)
    if AP.debug and self.param then
      local ok, d1, d2 = pcall(self.param.GetMap, self.param)
      emit("DEBUG", "MapLink.ExecuteChangeMap", tostring(ok), type(d1), tostring(d1), tostring(d2))
    end
    if AP.active and AP.locked and self.param then
      local ok, dest = pcall(self.param.GetMap, self.param)
      local dr, cr = regionOf(ok and dest), regionOf(TO13.Map.GetName())
      if dr and dr ~= cr and AP.locked[dr] then
        local now = AP.tick or 0
        if now >= AP.lockCooldown then
          AP.lockCooldown = now + 30 -- ~3 s at the 10 Hz poll rate
          emit("BLOCKED", dr, tostring(dest))
          AP.msgs[#AP.msgs + 1] = AP.lockText and AP.lockText[dr] or ("This region is locked by Archipelago (" .. dr .. ").")
        end
        return
      end
    end
    return orig(self, ...)
  end
end

-- Diagnostics: log every ChangeMap entry point (enabled with AP.debug = true).
do
  local CM = TO13.ChangeMap
  for _, fname in ipairs({ "Initialize", "InitializeLink", "InitializeReturn", "InitializeReload", "InitializeRestart", "InitializeRestore" }) do
    local orig = keepOrig("CM." .. fname, CM[fname])
    if type(orig) == "function" then
      local ok = pcall(function()
        CM[fname] = function(...)
          if AP.debug then
            local parts = { "DEBUG", "ChangeMap." .. fname }
            for i = 1, select("#", ...) do
              local v = select(i, ...)
              parts[#parts + 1] = type(v) .. ":" .. tostring(v)
            end
            AP.out[#AP.out + 1] = table.concat(parts, "\t")
          end
          return orig(...)
        end
      end)
      if not ok then emit("DEBUG", "cannot wrap ChangeMap." .. fname) end
    end
  end
end

-- ---------------------------------------------------------------------------
-- Message display. Message windows must run inside an engine script thread.
-- Game modules refuse new keys, so we borrow an existing method: SystemWarp.Execute
-- (the dungeon-return button handler). When AP.threadMode is set, the wrapped
-- method shows our queued messages instead; otherwise it behaves exactly as before.
-- ---------------------------------------------------------------------------
local SystemWarp = require("SystemWarp")
do
  local orig = keepOrig("SystemWarp.Execute", SystemWarp.Execute)
  SystemWarp.Execute = function(self, ...)
    if AP.threadMode then
      AP.threadMode = false
      while #AP.msgs > 0 do
        if not TO13.SystemWindow.IsInitialized() then
          SysWin.ShowMessage(table.remove(AP.msgs, 1))
        else
          coroutine.yield()
        end
      end
      AP.presenting = false
      return true
    end
    return orig(self, ...)
  end
end

local function pumpMessages()
  if AP.presenting or #AP.msgs == 0 then return end
  if not TO13.GameState.IsPlayerFree() then return end
  AP.host = AP.host or TO13.ScriptTask.Create("BerseriaAP")
  AP.hostObj = AP.hostObj or NTS.Script.CreateObject("SystemWarp")
  AP.presenting = true
  AP.threadMode = true
  local ok, err = pcall(AP.host.AddThread, AP.host, AP.hostObj, "Execute")
  if not ok then
    AP.presenting, AP.threadMode = false, false
    emit("ERROR", "message thread: " .. tostring(err))
  end
end

-- ---------------------------------------------------------------------------
-- Item delivery
-- ---------------------------------------------------------------------------
-- kind: "gald" | "game" (engine decides equipment vs. stackable) | "item" | "equip"
function AP.Give(kind, id, count)
  count = count or 1
  if kind == "game" then
    kind = TO13.Item.IsItemEquipmentCategory(id) and "equip" or "item"
  end
  if kind == "gald" then
    TO13.PartyProfile.AddGald(count)
  elseif kind == "equip" then
    for _ = 1, count do
      if TO13.Item.GetItemWorkDataEmptyNum() <= 0 then return false, "equipment storage full" end
      local wd = TO13.Item.CreateTreasureItemWorkData(id, 1, math.random(1, 65536), TO13.BattleAbilityData.ABL_NONE, 1)
      TO13.Item.AddItemWorkData(wd)
    end
  else
    TO13.Item.AddItemNum(id, count)
  end
  return true
end

-- Received-item counter lives in the save file (debug chest #1's unused ints), so
-- loading an older save re-delivers exactly what that save is missing.
function AP.GetSaveState()
  local d = TBSave.GetData(1)
  return d.lastest, d.random
end

function AP.SetSaveState(count, seedHash)
  local d = TBSave.GetData(1)
  d.lastest = count
  if seedHash then d.random = seedHash end
end

function AP.CanReceive()
  return TO13.GameState.IsPlayerFree() and TO13.GameState.Is(TO13.GameState.GAMESTATE_GAME)
end

function AP.Notify(text)
  AP.msgs[#AP.msgs + 1] = text
end

-- Called by C# every ~100 ms. Returns newline-separated events.
function AP.Poll()
  AP.tick = (AP.tick or 0) + 1
  pumpMessages()
  local sc = TO13.Scenario.GetScenarioFlag()
  if sc ~= AP.lastScenario then
    AP.lastScenario = sc
    emit("SCENARIO", sc)
  end
  if #AP.out == 0 then return "" end
  local s = table.concat(AP.out, "\n")
  AP.out = {}
  return s
end

-- Which chests/boxes are already opened in this save (used to resync checks).
function AP.OpenedList()
  local r = {}
  for rec in TO13.Database.TreasureBox.Collections, nil, nil do
    local d = TBSave.GetData(rec.Id)
    if d and d.first and rec.Id ~= 1 then r[#r + 1] = "TB:" .. rec.Id end
  end
  for rec in TO13.Database.CatBox.Collections, nil, nil do
    local d = CBSave.GetData(rec.Id)
    if d and d.opened then r[#r + 1] = "CB:" .. rec.Id end
  end
  if AP.sparkles then
    local TPSave = require("TO13TreasurePointSaveData")
    for i = 1, TO13.Database.TreasurePoint.GetMaxId() do
      local ok, d = pcall(TPSave.GetData, i)
      if ok and d and d.discover then r[#r + 1] = "TP:" .. i end
    end
  end
  return table.concat(r, ",")
end

return "runtime v" .. AP.version .. " loaded"

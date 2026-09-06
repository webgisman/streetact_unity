-- Player Profiles
CREATE TABLE IF NOT EXISTS public.player_profiles (
    id UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    username TEXT NOT NULL,
    action_points INTEGER DEFAULT 0,
    last_daily_reward_at TIMESTAMPTZ DEFAULT NOW(),
    created_at TIMESTAMPTZ DEFAULT NOW()
);
ALTER TABLE public.player_profiles ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Users can read own profile" ON public.player_profiles;
DROP POLICY IF EXISTS "Users can update own profile" ON public.player_profiles;
DROP POLICY IF EXISTS "Users can insert own profile" ON public.player_profiles;
CREATE POLICY "Users can read own profile" ON public.player_profiles FOR SELECT USING (auth.uid() = id);
CREATE POLICY "Users can update own profile" ON public.player_profiles FOR UPDATE USING (auth.uid() = id);
CREATE POLICY "Users can insert own profile" ON public.player_profiles FOR INSERT WITH CHECK (auth.uid() = id);

-- Player Roster (Inventory of units)
CREATE TABLE IF NOT EXISTS public.player_roster (
    user_id UUID REFERENCES auth.users(id) ON DELETE CASCADE,
    unit_type TEXT NOT NULL,
    quantity INTEGER DEFAULT 0,
    PRIMARY KEY (user_id, unit_type)
);
ALTER TABLE public.player_roster ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Users can read own roster" ON public.player_roster;
DROP POLICY IF EXISTS "Users can update own roster" ON public.player_roster;
DROP POLICY IF EXISTS "Users can insert own roster" ON public.player_roster;
DROP POLICY IF EXISTS "Users can delete own roster" ON public.player_roster;
CREATE POLICY "Users can read own roster" ON public.player_roster FOR SELECT USING (auth.uid() = user_id);
CREATE POLICY "Users can update own roster" ON public.player_roster FOR UPDATE USING (auth.uid() = user_id);
CREATE POLICY "Users can insert own roster" ON public.player_roster FOR INSERT WITH CHECK (auth.uid() = user_id);
CREATE POLICY "Users can delete own roster" ON public.player_roster FOR DELETE USING (auth.uid() = user_id);

-- Player Buildings (HQ per zone)
CREATE TABLE IF NOT EXISTS public.player_buildings (
    zone_id TEXT PRIMARY KEY,
    user_id UUID REFERENCES auth.users(id) ON DELETE CASCADE,
    building_index INTEGER NOT NULL,
    captured_at TIMESTAMPTZ DEFAULT NOW(),
    last_collection_at TIMESTAMPTZ DEFAULT NOW()
);
ALTER TABLE public.player_buildings ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Anyone can read buildings" ON public.player_buildings;
DROP POLICY IF EXISTS "Auth users can update buildings" ON public.player_buildings;
DROP POLICY IF EXISTS "Auth users can insert buildings" ON public.player_buildings;
DROP POLICY IF EXISTS "Auth users can delete buildings" ON public.player_buildings;
CREATE POLICY "Anyone can read buildings" ON public.player_buildings FOR SELECT USING (true);
CREATE POLICY "Auth users can update buildings" ON public.player_buildings FOR UPDATE USING (auth.role() = 'authenticated');
CREATE POLICY "Auth users can insert buildings" ON public.player_buildings FOR INSERT WITH CHECK (auth.role() = 'authenticated');
CREATE POLICY "Auth users can delete buildings" ON public.player_buildings FOR DELETE USING (auth.role() = 'authenticated');

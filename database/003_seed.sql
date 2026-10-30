/* =============================================================================
   Prima Three Acre Farms - Asset Finder
   003_seed.sql : SYNTHETIC demo assets so a fresh install has something to look up.
   Inserted only when dbo.Assets is empty. User accounts are NOT seeded here
   (passwords never live in SQL scripts) - Development creates them from configuration.
   ============================================================================= */
SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Assets)
BEGIN
    INSERT dbo.Assets (AssetCode, Farm, Department, AssetName, DetailName, Quantity, Remarks)
    VALUES
    (60110630, N'Kosgama',     N'PRODN', N'Automatic Feeder Line',  N'Pan feeder line with motor and hopper, House 1',         1, N'Serviced Jan 2026'),
    (60110631, N'Kosgama',     N'PRODN', N'Nipple Drinker Line',    N'Nipple drinker line with regulator, House 1',           4, NULL),
    (60110710, N'Kosgama',     N'GENRL', N'Generator 60 kVA',       N'Diesel standby generator with ATS panel',               1, N'Main power backup'),
    (60120105, N'Eswatta',     N'PRODN', N'Egg Collection Belt',    N'Motorised egg belt conveyor, House 3',                  1, NULL),
    (60120230, N'Eswatta',     N'GENRL', N'Water Pump 3 HP',        N'Submersible borehole pump with control box',            2, N'One unit kept as spare'),
    (60130044, N'Makuluwatta', N'PRODN', N'Brooder Heater',         N'Gas brooder heater, 8 kW',                              6, NULL),
    (60130101, N'Makuluwatta', N'GENRL', N'Office Computer',        N'Desktop PC with UPS - farm office',                      1, NULL),
    (60140017, N'Halwathura',  N'PRODN', N'Tunnel Ventilation Fan', N'50 inch exhaust fan with shutter',                      8, N'2 fans need belt replacement'),
    (60150300, N'Meegoda',     N'GENRL', N'Weighing Scale 300 kg',  N'Platform scale for feed bags',                          1, N'Calibrated Mar 2026'),
    (60150301, N'Meegoda',     N'PRODN', N'Egg Grading Table',      N'Stainless steel grading table',                         2, NULL),
    (60160012, N'Wewalpanawa', N'GENRL', N'Tractor Trailer',        N'Two-wheel trailer, 3 tonne',                            1, NULL),
    (60170045, N'Ittapana',    N'PRODN', N'Cooling Pad System',     N'Evaporative cooling pad with recirculation pump',       1, NULL),
    (60180009, N'Nilambe',     N'GENRL', N'Fire Extinguisher',      N'CO2 extinguisher 5 kg',                                 12, N'Refill due Dec 2026');
END
GO

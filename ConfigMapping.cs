using RadioConsole.Protocol;

namespace rc2_core
{
    public class ConfigMapping
    {
        /// <summary>
        /// A mapping of config text strings in the YAML and the protocol softkey name they map to
        /// </summary>
        private static Dictionary<String, SoftkeyName> SoftkeyNameMap = new Dictionary<string, SoftkeyName>
        {
            {"CALL", SoftkeyName.SoftkeyCall},
            {"CHAN", SoftkeyName.SoftkeyChan},
            {"CHUP", SoftkeyName.SoftkeyChup},
            {"CHDN", SoftkeyName.SoftkeyChdn},
            {"DEL", SoftkeyName.SoftkeyDel},
            {"DIR", SoftkeyName.SoftkeyDir},
            {"EMER", SoftkeyName.SoftkeyEmer},
            {"DYNP", SoftkeyName.SoftkeyDynp},
            {"HOME", SoftkeyName.SoftkeyHome},
            {"LOCK", SoftkeyName.SoftkeyLock},
            {"LPWR", SoftkeyName.SoftkeyLpwr},
            {"MON", SoftkeyName.SoftkeyMon},
            {"PAGE", SoftkeyName.SoftkeyPage},
            {"PHON", SoftkeyName.SoftkeyPhon},
            {"RAB1", SoftkeyName.SoftkeyRab1},
            {"RAB2", SoftkeyName.SoftkeyRab2},
            {"RCL", SoftkeyName.SoftkeyRcl},
            {"SCAN", SoftkeyName.SoftkeyScan},
            {"SEC", SoftkeyName.SoftkeySec},
            {"SEL", SoftkeyName.SoftkeySel},
            {"SITE", SoftkeyName.SoftkeySite},
            {"TCH1", SoftkeyName.SoftkeyTch1},
            {"TCH2", SoftkeyName.SoftkeyTch2},
            {"TCH3", SoftkeyName.SoftkeyTch3},
            {"TCH4", SoftkeyName.SoftkeyTch4},
            {"TGRP", SoftkeyName.SoftkeyTgrp},
            {"TMS", SoftkeyName.SoftkeyTms},
            {"TMSQ", SoftkeyName.SoftkeyTmsq},
            {"ZNUP", SoftkeyName.SoftkeyZnup},
            {"ZNDN", SoftkeyName.SoftkeyZndn},
            {"ZONE", SoftkeyName.SoftkeyZone}
        };

        /// <summary>
        /// Parses a config-style softkey name (MON, SCAN, etc) to the Protobuf Softkey name enum
        /// </summary>
        /// <param name="configName"></param>
        /// <returns></returns>
        public static SoftkeyName GetSoftkeyName(string keyName)
        {
            if (!SoftkeyNameMap.ContainsKey(keyName))
            {
                throw new ArgumentException($"No softkey lookup for softkey name {keyName}");
            }
            return SoftkeyNameMap[keyName];
        }
    }
}
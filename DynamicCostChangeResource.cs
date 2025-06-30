using System;
using System.Collections.Generic;

using System.Linq;

using System.Text.RegularExpressions;
using Skyline.DataMiner.Automation;
using Skyline.DataMiner.Library;
using Skyline.DataMiner.Library.Solutions.SRM;

using Skyline.DataMiner.Net.Messages;
using Skyline.DataMiner.Net.Messages.SLDataGateway;
using Skyline.DataMiner.Net.ResourceManager.Objects;


/// <summary>
/// Represents a DataMiner Automation script.
/// </summary>
public class Script
{
	private const int AlarmId = 65006; // Correlation Alarm Info
	private string _iDX;	
	private string alarmValue;
	private static SrmCache cache;
	private static Element _switchElement;
	private static string _sDestinationName;
	private static List<Resource> switchInterfaceResources;

	private static readonly Dictionary<string, Delegate> _executeActions = new Dictionary<string, Delegate>
		{
            // Add any actions that need to be executed here.
            // Example: { "ActionName", (Action)YourMethod }
            {"Escalated above", new Action<IEngine,string>((engine, cost) => UpdateResourceIncreaseCost(engine, cost)) },
			{"Dropped below", new Action<IEngine,string>((engine, cost) => UpdateResourceReduceCost(engine, cost)) }
		};

	private class ResourceCostInfo
	{
		public Resource Resource { get; set; }
		public ResourceManagerProperty CostProperty { get; set; }
		public int Cost { get; set; }
	}

	/// <summary>
	/// The script entry point.
	/// </summary>
	/// <param name="engine">Link with SLAutomation process.</param>
	public void Run(IEngine engine)
	{
		try
		{
			RunSafe(engine);
		}
		catch (ScriptAbortException)
		{
			// Catch normal abort exceptions (engine.ExitFail or engine.ExitSuccess)
			throw; // Uncomment if it should be treated as a normal exit of the script.
		}
		catch (ScriptForceAbortException)
		{
			// Catch forced abort exceptions, caused via external maintenance messages.
			throw;
		}
		catch (ScriptTimeoutException)
		{
			// Catch timeout exceptions for when a script has been running for too long.
			throw;
		}
		catch (InteractiveUserDetachedException)
		{
			// Catch a user detaching from the interactive script by closing the window.
			// Only applicable for interactive scripts, can be removed for non-interactive scripts.
			throw;
		}
		catch (Exception e)
		{
			engine.ExitFail("Run|Something went wrong: " + e);
		}
	}

	private void RunSafe(IEngine engine)
	{
		RetrieveAlarmValues(engine);

		Init(engine, _iDX);

		// Check and execute the corresponding action
		if (string.IsNullOrEmpty(alarmValue))
		{
			return;
		}

		//alarmValue  examples Escalated above 51.0 % or Dropped below 50.0 %
		//Keys do not contains the percentage value
		
			foreach (var key in _executeActions.Keys)
			{
				if (alarmValue.Contains(key, StringComparison.OrdinalIgnoreCase))
				{
					_executeActions[key].DynamicInvoke(engine, _iDX);
					break;
				}
			}
		
	}

	private void RetrieveAlarmValues(IEngine engine)
	{
		ScriptParam paramCorrelationAlarmInfo = engine.GetScriptParam(AlarmId);
		string alarmInfo = paramCorrelationAlarmInfo.Value;
		string[] parts = alarmInfo.Split('|');
		if (parts.Length < 11)
		{
			engine.GenerateInformation($"Invalid alarm info format: {alarmInfo}");
			engine.ExitFail($"Invalid alarm info format: {alarmInfo}");
		}
		
		_iDX = Convert.ToString(parts[4]);

		alarmValue = parts[10];
	}

	private void Init(IEngine engine, string _iDX)
	{
		cache = new SrmCache();
		string[] split = _iDX.Split('.');
		if (split.Length < 2)
		{
			engine.GenerateInformation($"Invalid _iDX format: {_iDX}");
			return;
		}

		string sSwitchName = split[0];
		_switchElement = engine.FindElement(sSwitchName);

		// This is needed to get parameter values from tables, the above cannot retrieve them easily
		if (_switchElement == null)
		{
			engine.ExitFail($"Element not found: { _switchElement.Name}");
			return;
		}

		;
		if (!TryExtractDestinationDeviceName(engine, split[1], out _sDestinationName))
		{
			engine.GenerateInformation($"Destination device name could not be extracted from: {_iDX} can be an edge device which doesn´t require a cost update");
		}
	}

	public static bool TryExtractDestinationDeviceName(IEngine engine, string input, out string output)
	{
		// Regex covers: <interface> <device>[-Eth...], <interface> : LEAF <device>, etc.
		//examples of data Ethernet1/27/SPINE MM-HE-CS64-SPINE1-Eth1/6 10
		//Ethernet1 / 6 / LEAF MM - HE - CS36 - LEAF1 - Eth1 / 27 10
		//Ethernet29 / 1 : LEAF SX-266 - AS06 - LEAF2 - Et50 / 1 10
		//Ethernet27 / 1 : LEAF TEULADA-LAB - LEAF1 192
		//Ethernet1 / 1 / p2p to NX93180-SX - LEAF - 01 - eth1 / 49
		//Ethernet1 / 49 / p2p to spine C9336 - eth1 / 1

		var match = Regex.Match(input, @"(?:\b(?:LEAF|SPINE)[^ ]*\s+|^|to\s+)([A-Z0-9\-]+-(?:LEAF\d+|SPINE\d+|LEAF-\d+|SPINE-\d+|LEAF|SPINE))\b", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			output = match.Groups[1].Value;
			return true;
		}
		// Fallback for cases like "to <device> - eth" mainly seen on cisco lab
		match = Regex.Match(input, @"to\s+([A-Za-z0-9\- ]+?)(?=\s*-\s*eth|\s*$)", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			output = match.Groups[1].Value.Trim();
			return true;
		}
		output = string.Empty;
		return false;
	}

	private static void UpdateResourceIncreaseCost(IEngine engine, string alarmPortValue)
	{
		List<Resource> listResourcetoAnotherSwitch = UpdateOtherPortCost(engine, alarmPortValue);		

		if (listResourcetoAnotherSwitch == null || listResourcetoAnotherSwitch.Count == 0)
		{
			engine.GenerateInformation($"No resources found for the destination: {_sDestinationName}");
			return;
		}

		foreach (var resource in listResourcetoAnotherSwitch)
		{
			engine.GenerateInformation($"Resource {resource.Name} cost updated to {resource.Properties.Find(x => x.Name == "Cost")?.Value}");
		}
		SrmManagers.ResourceManager.AddOrUpdateResources(listResourcetoAnotherSwitch.ToArray());
	}

	private static void UpdateResourceReduceCost(IEngine engine, string alarmPortValue)
	{
		List<Resource> listResourcetoUpdate = UpdatePreviousPortCost(engine, alarmPortValue);

		SrmManagers.ResourceManager.AddOrUpdateResources(listResourcetoUpdate.ToArray());
	}

	private static List<Resource> RetrieveOtherSwitchDestinationResources(IEngine engine)
	{
		var allRawInterfaceResources = GetSwitchInterfaces(engine, _switchElement, cache);

		switchInterfaceResources = allRawInterfaceResources.Where(r => r.Name.Contains(_sDestinationName)).ToList();
		return switchInterfaceResources;
	}

	public static IEnumerable<Resource> GetSwitchInterfaces(IEngine engine, Element element, SrmCache cache)
	{
		ResourcePool resourcePool;

		switch (element.Protocol.Name)
		{
			case "Arista Manager": resourcePool = cache.GetResourcePoolByName("Arista Network"); break;
			case "CISCO Nexus": resourcePool = cache.GetResourcePoolByName("Cisco Network"); break;
			default: throw new NotSupportedException("Unsupported switch connector");
		}

		var filter = new ANDFilterElement<Resource>(
			ResourceExposers.PoolGUIDs.Contains(resourcePool.GUID),
			FunctionResourceExposers.MainDVEDmaID.Equal(element.DmaId),
			FunctionResourceExposers.MainDVEElementID.Equal(element.ElementId)
		);

		var resources = SrmManagers.ResourceManager.GetResources(filter);

		return resources;
	}

	private static List<Resource> UpdateOtherPortCost(IEngine engine, string sAlarmCostUpdate)
	{
		List<Resource> lResourcesSameDestination = RetrieveOtherSwitchDestinationResources(engine);

		if (lResourcesSameDestination == null || lResourcesSameDestination.Count == 0)
		{
			engine.GenerateInformation($"No resources found with the destination: {_sDestinationName}");
			return new List<Resource>();
		}

		List<ResourceCostInfo> resourceCosts = GetResourceCostsBellow100(lResourcesSameDestination);

		if (resourceCosts.Count == 0)
			return new List<Resource>();

		// Find the resources with name equal to baseCostString
		int previousValue;

		if (!TryGetAndUpdateTargetResources(resourceCosts, sAlarmCostUpdate, engine, out previousValue, out var targets))
		{
			return new List<Resource>();
		}

		// Find the next resource with the lowest value (ignoring the ones already updated)
		var (nextResources, nextPreviousValue, diff) = GetNextResourcesAndDiff(resourceCosts, targets, previousValue, engine);
		if (nextResources == null || nextResources.Count == 0)
		{
			return targets.Select(x => x.Resource).ToList();
		}

		foreach (var otherResourcePort in nextResources)
		{
			// If the cost is equal to the next previous value, set it to the previous value
			// Otherwise, increment it by the difference

			otherResourcePort.CostProperty.Value = (otherResourcePort.Cost == nextPreviousValue) ? Convert.ToString(previousValue) : Convert.ToString(previousValue + diff);
		}

		// Return the updated list of resources
		return resourceCosts.Select(x => x.Resource).ToList();
	}

	private static List<ResourceCostInfo> GetResourceCostsBellow100(List<Resource> resources)
	{
		return resources
			.Select(r => new ResourceCostInfo
			{
				Resource = r,
				CostProperty = r.Properties.Find(x => x.Name == "Cost"),
				Cost = int.TryParse(r.Properties.Find(x => x.Name == "Cost")?.Value, out int cost) ? cost : int.MaxValue
			})
			.Where(x => x.CostProperty != null && !string.IsNullOrEmpty(x.CostProperty.Value) && x.Cost < 100)
			.OrderBy(x => x.Cost)
			.ToList();
	}

	private static bool TryGetAndUpdateTargetResources(List<ResourceCostInfo> resourceCosts, string sAlarmCostUpdate, IEngine engine, out int previousValue, out List<ResourceCostInfo> targets)
	{
		previousValue = 0;
		string baseCostString = sAlarmCostUpdate;
		int lastDot = baseCostString.LastIndexOf('.');
		if (lastDot != -1)
		{
			baseCostString = baseCostString.Substring(0, lastDot);
		}

		targets = resourceCosts
			.Where(x => x.Resource.Name.Contains(baseCostString, StringComparison.OrdinalIgnoreCase))
			.Take(2)
			.ToList();

		if (targets == null || targets.Count == 0)
		{
			engine.GenerateInformation($"No target resources found for the base cost string: {baseCostString}");			
			return false;
		}

		foreach (var target in targets)
		{			
			previousValue = target.Cost;
			target.CostProperty.Value = "100";
		}

		return true;
	}

	private static (List<ResourceCostInfo> nextResources, int nextPreviousValue, int diff) GetNextResourcesAndDiff(List<ResourceCostInfo> resourceCosts, List<ResourceCostInfo> targets, int previousValue, IEngine engine)
	{
		var next = resourceCosts
			.Where(x => !targets.Any(t => t.Resource == x.Resource))
			.OrderBy(x => x.Cost)
			.FirstOrDefault();

		var nextResources = resourceCosts
			.Where(x => !targets.Any(t => t.Resource == x.Resource))
			.OrderBy(x => x.Cost)
			.ToList();

		if (next == null)
		{
			engine.GenerateInformation("No next resource found for the cost update.");
			return (nextResources, 0, 0);
		}

		int nextPreviousValue = next.Cost;
		int diff = nextPreviousValue - previousValue;
		return (nextResources, nextPreviousValue, diff);
	}

	private static List<Resource> UpdatePreviousPortCost(IEngine engine, string sCurrentResource)
	{
		List<Resource> lResourcesSameDestination = RetrieveOtherSwitchDestinationResources(engine);

		if (lResourcesSameDestination == null && lResourcesSameDestination.Count == 0)
		{
			engine.GenerateInformation($"No resources found with the destination: {_sDestinationName}");
			return new List<Resource>();
		}

		List<ResourceCostInfo> resourceCosts = GetResourceCostsBelowOrEqual100(lResourcesSameDestination);

		if (resourceCosts.Count == 0)
			return new List<Resource>();

		string baseCostString = sCurrentResource;
		var (targets, iMinimumCost) = GetTargetsAndMinimumCost(resourceCosts, sCurrentResource);
		int diff = 10;
		if (targets != null)
		{
			targets.ForEach(target => target.CostProperty.Value = (target.Cost == 100 && iMinimumCost < 100) ? Convert.ToString(iMinimumCost + diff) : Convert.ToString(diff));
		}

		// Return the updated list of resources
		return resourceCosts.Select(x => x.Resource).ToList();
	}

	private static List<ResourceCostInfo> GetResourceCostsBelowOrEqual100(List<Resource> resources)
	{
		return resources
			.Select(r => new ResourceCostInfo
			{
				Resource = r,
				CostProperty = r.Properties.Find(x => x.Name == "Cost"),
				Cost = int.TryParse(r.Properties.Find(x => x.Name == "Cost")?.Value, out int cost) ? cost : int.MaxValue
			})
			.Where(x => x.CostProperty != null && !string.IsNullOrEmpty(x.CostProperty.Value) && x.Cost <= 100)
			.OrderBy(x => x.Cost)
			.ToList();
	}

	private static (List<ResourceCostInfo> targets, int iMinimumCost) GetTargetsAndMinimumCost(List<ResourceCostInfo> resourceCosts, string sCurrentResource)
	{
		string baseCostString = sCurrentResource;
		int lastDot = baseCostString.LastIndexOf('.');
		if (lastDot != -1)
		{
			baseCostString = baseCostString.Substring(0, lastDot);
		}

		var targets = resourceCosts
			.Where(x => x.Resource.Name.Contains(baseCostString, StringComparison.OrdinalIgnoreCase))
			.Take(2)
			.ToList();

		int iMinimumCost = resourceCosts
			.Where(x => !targets.Any(t => t.Resource == x.Resource))
			.OrderBy(x => x.Cost)
			.FirstOrDefault()?.Cost ?? 0;

		return (targets, iMinimumCost);
	}
}
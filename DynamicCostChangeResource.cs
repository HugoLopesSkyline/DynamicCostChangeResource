using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using Skyline.DataMiner.Automation;
using Skyline.DataMiner.Net.Messages.SLDataGateway;
using Skyline.DataMiner.Net;
using SLDataGateway.Caching;
using Skyline.DataMiner.Library;
using Skyline.DataMiner.Library.Solutions.SRM;
using Skyline.DataMiner.Net.Messages;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions;
using Skyline.DataMiner.Net.ResourceManager.Objects;
using System.Configuration;
using static System.Net.Mime.MediaTypeNames;

namespace DynamicCostChangeResource
{
	/// <summary>
	/// Represents a DataMiner Automation script.
	/// </summary>
	public class Script
	{

		private const int AlarmId = 65006; // Correlation Alarm Info
		private string _iDX;
		private int _parameterID;
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
			ScriptParam paramCorrelationAlarmInfo = engine.GetScriptParam(AlarmId);
			string alarmInfo = paramCorrelationAlarmInfo.Value;
			string[] parts = alarmInfo.Split('|');
			if (parts.Length < 11)
			{
				engine.GenerateInformation("Invalid alarm info format: " + alarmInfo);
				return;
			}

			int dmaID = Convert.ToInt32(parts[1]);
			int elementID = Convert.ToInt32(parts[2]);
			_parameterID = Convert.ToInt32(parts[3]);
			_iDX = Convert.ToString(parts[4]);

			alarmValue = parts[10];

			engine.GenerateInformation("Display Key: " + _iDX);
			engine.GenerateInformation("Alarm : " + alarmValue);

			Init(engine, _iDX);

			// Check and execute the corresponding action
			if (!string.IsNullOrEmpty(alarmValue))
			{
				foreach (var key in _executeActions.Keys)
				{
					if (alarmValue.Contains(key, StringComparison.OrdinalIgnoreCase))
					{
						_executeActions[key].DynamicInvoke(engine, _iDX);
						break;
					}
				}
			}
		}

		private void Init(IEngine engine, string _iDX)
		{
			cache = new SrmCache();
			if (_iDX.Contains('.'))
			{
				string sSwitchName = _iDX.Split('.')[0];
				_switchElement = engine.FindElement(sSwitchName);
			}
			// This is needed to get parameter values from tables, the above cannot retrieve them easily
			if (_switchElement == null)
				engine.ExitFail("Element not found: " + _switchElement.Name);
			
			_sDestinationName = ExtractDestinationDeviceName(engine, _iDX.Split('.')[1]);
			if (string.IsNullOrEmpty(_sDestinationName))
			{
				engine.GenerateInformation("Destination device name could not be extracted from: " + _iDX+" can be an edge device which doesn´t require a cost update ");
			}
		}

		public static string ExtractDestinationDeviceName(IEngine engine, string input)
		{
			// Regex covers: <interface> <device>[-Eth...], <interface> : LEAF <device>, etc.
			
			var match = Regex.Match(input, @"(?:\b(?:LEAF|SPINE)[^ ]*\s+|^|to\s+)([A-Z0-9\-]+-(?:LEAF\d+|SPINE\d+|LEAF-\d+|SPINE-\d+|LEAF|SPINE))\b", RegexOptions.IgnoreCase);
			if (match.Success)
			{
				return match.Groups[1].Value;
			}
			// Fallback for cases like "to <device> - eth" mainly seen on cisco lab
			match = Regex.Match(input, @"to\s+([A-Za-z0-9\- ]+?)(?=\s*-\s*eth|\s*$)", RegexOptions.IgnoreCase);
			if (match.Success)
			{
				return match.Groups[1].Value.Trim();
			}
			return null;
		}

		private static void UpdateResourceIncreaseCost(IEngine engine, string alarmPortValue)
		{		

			List<Resource> listResourcetoAnotherSwitch = new List<Resource>();

			listResourcetoAnotherSwitch = UpdateOtherPortCost(engine, alarmPortValue);

			var resource1 = SrmManagers.ResourceManager.AddOrUpdateResources(listResourcetoAnotherSwitch.ToArray());
		}

		private static void UpdateResourceReduceCost(IEngine engine, string alarmPortValue)
		{
			List<Resource> listResourcetoUpdate = new List<Resource>();


			listResourcetoUpdate = UpdatePreviousPortCost(engine, alarmPortValue);

			var resource1 = SrmManagers.ResourceManager.AddOrUpdateResources(listResourcetoUpdate.ToArray());
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

			if (lResourcesSameDestination != null && lResourcesSameDestination.Count > 0)
			{
				var resourceCosts = lResourcesSameDestination
					.Select(r => new
					{
						Resource = r,
						CostProperty = r.Properties.Find(x => x.Name == "Cost"),
					})
					.Where(x => x.CostProperty != null && !string.IsNullOrEmpty(x.CostProperty.Value)
						&& int.TryParse(x.CostProperty.Value, out int cost) && cost < 100)
					.Select(x => new
					{
						x.Resource,
						x.CostProperty,
						Cost = int.Parse(x.CostProperty.Value)
					})
					.OrderBy(x => x.Cost)
					.ToList();

				if (resourceCosts.Count == 0)
					return new List<Resource>();

				// Find the resources with name equal to baseCostString
				int previousValue = 0;
				string baseCostString = sAlarmCostUpdate;
				int lastDot = baseCostString.LastIndexOf('.');
				if (lastDot != -1)
				{
					baseCostString = baseCostString.Substring(0, lastDot);
				}
				
				var targets = resourceCosts.Where(x => x.Resource.Name.Contains(baseCostString, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();

				if (targets != null)
				{
					foreach (var target in targets)
					{
						engine.GenerateInformation("Resourcestring: " + target.Resource.Name);
						previousValue = target.Cost;
						target.CostProperty.Value = "100";
					}

					// Find the next resource with the lowest value (ignoring the ones already updated)
					var next = resourceCosts.Where(x => !targets.Any(t => t.Resource == x.Resource)).OrderBy(x => x.Cost).FirstOrDefault();
					var nextResources = resourceCosts.Where(x => !targets.Any(t => t.Resource == x.Resource)).OrderBy(x => x.Cost).ToList();
					if (next != null)
					{
						int nextPreviousValue = next.Cost;
						next.CostProperty.Value = Convert.ToString(previousValue);
						int diff = nextPreviousValue - previousValue;
						
						foreach (var otherResourcePort in nextResources)
						{
							if (otherResourcePort.Cost == nextPreviousValue)
							{
								otherResourcePort.CostProperty.Value = Convert.ToString(previousValue);
							}
							else
								otherResourcePort.CostProperty.Value = Convert.ToString(previousValue + diff);
						}
					}
				}

				// Return the updated list of resources
				return resourceCosts.Select(x => x.Resource).ToList();
			}

			return new List<Resource>();
		}

		private static List<Resource> UpdatePreviousPortCost(IEngine engine, string sCurrentResource)
		{
			List<Resource> lResourcesSameDestination = RetrieveOtherSwitchDestinationResources(engine);

			if (lResourcesSameDestination != null && lResourcesSameDestination.Count > 0)
			{
				var resourceCosts = lResourcesSameDestination
					.Select(r => new
					{
						Resource = r,
						CostProperty = r.Properties.Find(x => x.Name == "Cost"),
					})
					.Where(x => x.CostProperty != null && !string.IsNullOrEmpty(x.CostProperty.Value)
						&& int.TryParse(x.CostProperty.Value, out int cost) && cost <= 100)
					.Select(x => new
					{
						x.Resource,
						x.CostProperty,
						Cost = int.Parse(x.CostProperty.Value)
					})
					.OrderBy(x => x.Cost)
					.ToList();

				if (resourceCosts.Count == 0)
					return new List<Resource>();

				// Find the resources with name equal to baseCostString
				int previousValue = 0;
				string baseCostString = sCurrentResource;
				int lastDot = baseCostString.LastIndexOf('.');
				if (lastDot != -1)
				{
					baseCostString = baseCostString.Substring(0, lastDot);
				}
				
				var targets = resourceCosts.Where(x => x.Resource.Name.Contains(baseCostString, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
				int iMinimumCost =  resourceCosts.Where(x => !targets.Any(t => t.Resource == x.Resource)).OrderBy(x => x.Cost).FirstOrDefault().Cost;
				int diff = 10;
				if (targets != null)
				{
					foreach (var target in targets)
					{
						
						
							if (target.Cost == 100)
							{
							target.CostProperty.Value = Convert.ToString(iMinimumCost + diff);
							}					

					}									
					
					
				}

				// Return the updated list of resources
				return resourceCosts.Select(x => x.Resource).ToList();
			}

			return new List<Resource>();
		}

	}

}

using System;
using System.Collections.Generic;

namespace Maxisoft.ASF.Redlib.Instances;

public sealed class CachedRedlibInstanceListStorage(ICollection<Uri> instances, DateTimeOffset lastUpdate) {
	public ICollection<Uri> Instances { get; private set; } = instances;
	public DateTimeOffset LastUpdate { get; private set; } = lastUpdate;

	/// <summary>
	///     Updates the list of instances and its last update time
	/// </summary>
	/// <param name="instances">The list of instances to update</param>
	internal void UpdateInstances(ICollection<Uri> instances) {
		Instances = instances;
		LastUpdate = DateTimeOffset.Now;
	}
}

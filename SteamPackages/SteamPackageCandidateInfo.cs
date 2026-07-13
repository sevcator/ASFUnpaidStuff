using System;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;

namespace Maxisoft.ASF.SteamPackages;

internal sealed class SteamPackageCandidateInfo {
	public SteamPackageCandidateInfo(
		uint id,
		IReadOnlyCollection<uint> packageContentIds,
		EBillingType billingType,
		EPackageStatus status,
		ELicenseType licenseType,
		bool deactivatedDemo,
		ulong expiryTime,
		bool freeWeekend,
		bool betaTesterPackage
	) {
		Id = id;
		PackageContentIds = packageContentIds.Where(static appId => appId > 0).ToHashSet();
		BillingType = billingType;
		Status = status;
		LicenseType = licenseType;
		DeactivatedDemo = deactivatedDemo;
		ExpiryTime = expiryTime;
		FreeWeekend = freeWeekend;
		BetaTesterPackage = betaTesterPackage;
	}

	public uint Id { get; }
	public IReadOnlyCollection<uint> PackageContentIds { get; }
	public EBillingType BillingType { get; }
	public EPackageStatus Status { get; }
	public ELicenseType LicenseType { get; }
	public bool DeactivatedDemo { get; }
	public ulong ExpiryTime { get; }
	public bool FreeWeekend { get; }
	public bool BetaTesterPackage { get; }

	public static SteamPackageCandidateInfo FromProductInfo(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo) {
		KeyValue kv = productInfo.KeyValues;

		return new SteamPackageCandidateInfo(
			productInfo.ID,
			kv["appids"].Children.Select(static app => app.AsUnsignedInteger()).ToHashSet(),
			(EBillingType) kv["billingtype"].AsInteger(),
			(EPackageStatus) kv["status"].AsInteger(),
			(ELicenseType) kv["licensetype"].AsInteger(),
			kv["extended"]["deactivated_demo"].AsBoolean(),
			kv["extended"]["expirytime"].AsUnsignedLong(),
			kv["extended"]["freeweekend"].AsBoolean(),
			kv["extended"]["betatesterpackage"].AsBoolean()
		);
	}

	public bool IsFreeAvailablePackage(DateTimeOffset now) {
		if (PackageContentIds.Count == 0) {
			return false;
		}

		if (BillingType is not (EBillingType.FreeOnDemand or EBillingType.NoCost)) {
			return false;
		}

		if (Status is not EPackageStatus.Available) {
			return false;
		}

		if (LicenseType is not ELicenseType.SinglePurchase) {
			return false;
		}

		if ((ExpiryTime > 0) && (ExpiryTime < (ulong) now.ToUnixTimeSeconds())) {
			return false;
		}

		if (DeactivatedDemo || FreeWeekend || BetaTesterPackage) {
			return false;
		}

		return Id != 17906;
	}
}

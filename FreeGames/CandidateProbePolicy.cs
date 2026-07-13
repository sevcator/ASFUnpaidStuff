using System;

namespace Maxisoft.ASF.FreeGames;

[Flags]
internal enum EFreeGameCandidateSource : byte {
	None = 0,
	Reddit = 1,
	Steam = 2,
	SteamDb = 4
}

internal enum EProbeGameStatus : byte {
	Validated,
	NotIncremented,
	BadgeUnavailable,
	RateLimited,

	/// <summary>
	/// The probe account owned the license before the probe even started. The badge cannot increment for an
	/// already-owned game, so this must NOT be treated as a failed validation — the game is fanned out to the
	/// other bots (which may not own it yet). Bare ownership is NOT proof the game is claimable/badge-worthy
	/// though, so it is persisted as validated only after another bot actually claims it.
	/// </summary>
	AlreadyOwnedByProbe
}

internal readonly record struct ProbePersistenceDecision(
	bool SaveValidated,
	bool SaveSteamChecked,
	bool RemoveSteamPending,
	bool FanOut,
	bool Stop
);

internal static class CandidateProbePolicy {
	internal static ProbePersistenceDecision Decide(EFreeGameCandidateSource source, EProbeGameStatus status) {
		bool isSteam = source.HasFlag(EFreeGameCandidateSource.Steam) || source.HasFlag(EFreeGameCandidateSource.SteamDb);

		return status switch {
			EProbeGameStatus.Validated => new ProbePersistenceDecision(
				SaveValidated: true,
				SaveSteamChecked: isSteam,
				RemoveSteamPending: isSteam,
				FanOut: true,
				Stop: false
			),
			EProbeGameStatus.NotIncremented => new ProbePersistenceDecision(
				SaveValidated: false,
				SaveSteamChecked: isSteam,
				RemoveSteamPending: isSteam,
				FanOut: false,
				Stop: false
			),
			// SaveValidated stays false: the caller persists the id only once the fan-out succeeded on another bot
			EProbeGameStatus.AlreadyOwnedByProbe => new ProbePersistenceDecision(
				SaveValidated: false,
				SaveSteamChecked: isSteam,
				RemoveSteamPending: isSteam,
				FanOut: true,
				Stop: false
			),
			EProbeGameStatus.BadgeUnavailable => default(ProbePersistenceDecision),
			EProbeGameStatus.RateLimited => new ProbePersistenceDecision(
				SaveValidated: false,
				SaveSteamChecked: false,
				RemoveSteamPending: false,
				FanOut: false,
				Stop: true
			),
			_ => default(ProbePersistenceDecision)
		};
	}
}

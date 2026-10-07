use serde::{Deserialize, Serialize};

use crate::{ErrorKind, ProbeError, Result};

/// Includes every in-progress value recognized by the existing Mod, even where
/// today's updater does not emit it. These names are wire compatibility only.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum LegacyInstallState {
    Waiting,
    Preparing,
    ClosingCompanion,
    WaitingGame,
    TerminatingGame,
    GameClosed,
    BackingUp,
    Installing,
    Verifying,
    Succeeded,
    Failed,
    Cancelled,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LegacyInstallStatus {
    pub state: LegacyInstallState,
    pub message: String,
    pub progress: u8,
}

impl LegacyInstallStatus {
    pub fn to_json(&self) -> Result<Vec<u8>> {
        self.validate()?;
        serde_json::to_vec(self).map_err(|error| invalid(error.to_string()))
    }

    pub fn from_json(bytes: &[u8]) -> Result<Self> {
        if bytes.len() > 64 * 1024 {
            return Err(invalid("status exceeds probe's 64 KiB limit"));
        }
        let status: Self =
            serde_json::from_slice(bytes).map_err(|error| invalid(error.to_string()))?;
        status.validate()?;
        Ok(status)
    }

    fn validate(&self) -> Result<()> {
        if self.progress > 100 || self.message.len() > 32 * 1024 || self.message.contains('\0') {
            return Err(invalid("invalid progress or status message"));
        }
        Ok(())
    }
}

fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidStatus, message)
}

//! Read-only P0 contracts. Successful validation never authorizes installation.

pub mod archive;
pub mod bundle;
pub mod launch;
pub mod status;
pub mod wire;

mod paths;

use std::fmt;

pub const PRODUCT_NAME: &str = "mystia-steward-companion";
pub const UPDATER_FILE_NAME: &str = "mystia-steward-companion-updater.exe";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorKind {
    InvalidArguments,
    InvalidPath,
    InvalidManifest,
    InvalidStatus,
    IntegrityMismatch,
    Io,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProbeError {
    pub kind: ErrorKind,
    pub message: String,
}

impl ProbeError {
    pub(crate) fn new(kind: ErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
        }
    }
}

impl fmt::Display for ProbeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{:?}: {}", self.kind, self.message)
    }
}

impl std::error::Error for ProbeError {}

pub type Result<T> = std::result::Result<T, ProbeError>;

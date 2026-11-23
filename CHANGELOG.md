# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `Payment` aggregate with an explicit state machine, `Money` / `Currency` value objects (ISO-4217 minor units) and domain events
- Domain unit tests, including FsCheck property tests for money arithmetic, refunds and out-of-order provider updates
- CI workflow: build, unit tests, coverage summary

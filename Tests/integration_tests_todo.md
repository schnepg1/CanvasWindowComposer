# Integration tests

- Windows integration tests for mixed-DPI monitors, disconnected screens, ambiguous same-app windows and crash recovery.
- Your named-wrapper idea is partly implemented as named launch places. Generic wrappers and isolated app profiles aren’t added yet. : extract a testable placement planner, then add real Windows integration tests for DPI, monitor changes and recovery.
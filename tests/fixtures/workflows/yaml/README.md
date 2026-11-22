# Workflow YAML fixtures (plan 2.21)

The YAML twins of the five real exports in `../real/`: every step, column and literal of each export, in XAML order, with the
real process id. They are written in the canonical form of `WorkflowYaml.Write` (`Xrmd.Schema`): `WorkflowYamlTests.Write_of_read_fixture_is_the_same_text`
pins that, and the decompiler tests (plan 2.21, Task 4) assert that each real export decompiles to exactly this text, so edit
them only together with the writer.

| File | Twin of | Category |
| --- | --- | --- |
| `oncreate.yaml` | `real/OnCreate-….xaml` | real-time workflow: create a task, start the child |
| `onupdate.yaml` | `real/OnUpdate-….xaml` | background workflow: wait until, then send an email |
| `child.yaml` | `real/Child-….xaml` | real-time child process: stop (canceled) |
| `business-rule.yaml` | `real/Newbusinessrule-….xaml` | business rule: condition, set value, show error |
| `recalculate.yaml` | `real/Recalculate-….xaml` | action bound to `xrmv_order`, publisher `xrmv`: arguments, update, output, condition, stop |

The files use LF line ends (`.gitattributes`); the real XAML files keep their CRLF. Names, ids and data are those of the export
(invented, publisher `xrmv`); no customer or third-party content belongs here.

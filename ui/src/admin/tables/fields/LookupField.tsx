import {
    Button,
    DrawerBody,
    DrawerHeader,
    DrawerHeaderTitle,
    Field,
    Input,
    makeStyles,
    OverlayDrawer,
    Select,
    Text,
    tokens,
} from "@fluentui/react-components";
import { useCallback, useState } from "react";
import { useRecordOpener } from "../../../apps/record/recordOpener";
import { EntityPicker } from "../../common/EntityPicker";
import {
    DeleteIcon,
    DismissIcon,
    LookupSearchIcon,
} from "../../../shell/icons";
import { t } from "../../../shell/i18n/t";
import type { LookupValue } from "../formValues";
import { useLoad } from "../useLoad";
import { entityPath } from "../tableApi";
import { useFieldAppearance } from "./fieldAppearance";
import type { FieldProps } from "./fieldProps";

const useStyles = makeStyles({
    actions: { display: "flex", alignItems: "center" },
    // The value of a lookup that opens its record: a link, as in Dataverse.
    link: {
        color: tokens.colorBrandForegroundLink,
        textDecorationLine: "underline",
        cursor: "pointer",
    },
    body: {
        display: "flex",
        flexDirection: "column",
        gap: tokens.spacingVerticalM,
    },
});

interface TargetMeta {
    EntitySetName?: string;
    PrimaryIdAttribute?: string;
    PrimaryNameAttribute?: string;
}

function TargetPicker({
    attr,
    target,
    nav,
    value,
    onChange,
    preSearch,
}: {
    attr: FieldProps["attr"];
    target: string;
    nav?: string;
    value: LookupValue | null;
    onChange: FieldProps["onChange"];
    preSearch?: FieldProps["preSearch"];
}) {
    const filters = useCallback(
        () => preSearch?.(target) ?? [],
        [preSearch, target],
    );
    const meta = useLoad(target, (api) =>
        api.get<TargetMeta>(
            `${entityPath(target)}?$select=EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute`,
        ),
    );
    if (meta.error) return <Text role="alert">{meta.error}</Text>;
    const m = meta.data;
    if (!m) return <Text>{t("common.loading")}</Text>;
    if (!m.EntitySetName || !m.PrimaryNameAttribute)
        return (
            <Text role="alert">
                {t("field.lookup.notSearchable", { table: target })}
            </Text>
        );
    return (
        <EntityPicker
      layout="list"
            entitySet={m.EntitySetName}
            entityName={target}
            fetchFilters={preSearch ? filters : undefined}
            idAttribute={m.PrimaryIdAttribute ?? `${target}id`}
            nameAttribute={m.PrimaryNameAttribute}
            label={t("field.lookup.search", { label: attr.displayName })}
            selected={
                value && value.target === target
                    ? { id: value.id, name: value.name ?? value.id }
                    : undefined
            }
            onSelect={(picked) =>
                onChange({
                    id: picked.id,
                    name: picked.name,
                    target,
                    set: m.EntitySetName,
                    nav,
                })
            }
        />
    );
}

/**
 * `lookup` columns. The value is bound through the `ReferencingEntityNavigationPropertyName` of the lookup's relationship to
 * the chosen table. A lookup to several tables (customer, owner, regarding) asks for the table first.
 */
export function LookupField({
    attr,
    value,
    onChange,
    disabled,
    error,
    context,
    preSearch,
    entityTypes,
}: FieldProps) {
    const lookup =
        value && typeof value === "object" && !Array.isArray(value)
            ? value
            : null;
    const relationships = context.lookups.filter(
        (r) =>
            r.referencingAttribute === attr.logicalName &&
            (!entityTypes || entityTypes.includes(r.referencedEntity)),
    );
    const targets = [...new Set(relationships.map((r) => r.referencedEntity))];
    const styles = useStyles();
    const appearance = useFieldAppearance(disabled);
    const [choosing, setChoosing] = useState(false);
    const openRecord = useRecordOpener();
    // The name the opened record was saved with, shown without changing the form's value (which stays clean).
    const [renamed, setRenamed] = useState<{ id: string; name: string }>();
    const openable = !!openRecord && !!lookup?.target && !!lookup.id;
    const shown = lookup
        ? renamed && renamed.id === lookup.id
            ? renamed.name
            : (lookup.name ?? lookup.id)
        : "";
    const open = (event: React.MouseEvent<HTMLElement> | React.KeyboardEvent<HTMLElement>) => {
        if (!openRecord || !lookup?.target) return;
        event.preventDefault();
        void openRecord(
            { etn: lookup.target, id: lookup.id },
            { event, anchor: event.currentTarget },
        ).then((saved) => {
            if (saved?.name && saved.id.toLowerCase() === lookup.id.toLowerCase()) setRenamed({ id: lookup.id, name: saved.name });
        });
    };
    const [target, setTarget] = useState(
        lookup?.target ?? (targets.length === 1 ? targets[0] : ""),
    );

    if (relationships.length === 0) {
        // Without a relationship there is no navigation property to bind with, and none is guessed.
        return (
            <Field
                label={attr.displayName}
                validationMessage={t("field.lookup.noRelationship")}
                validationState="error"
            >
                <Text>
                    {lookup ? (lookup.name ?? lookup.id) : t("field.empty")}
                </Text>
            </Field>
        );
    }
    const nav = relationships.find((r) => r.referencedEntity === target)?.nav;
    const pick: FieldProps["onChange"] = (v) => {
        onChange(v);
        setChoosing(false);
    };
    return (
        <>
            <Field
                label={attr.displayName}
                required={attr.required}
                validationMessage={error}
            >
                <Input
                    appearance={appearance}
                    readOnly
                    // A read-only lookup still opens its record: the input stays focusable and clickable.
                    disabled={disabled && !openable}
                    aria-readonly={disabled || undefined}
                    input={
                        openable
                            ? {
                                  className: styles.link,
                                  title: t("field.lookup.open", { name: shown }),
                                  onClick: open,
                                  onKeyDown: (e) => e.key === "Enter" && open(e),
                              }
                            : undefined
                    }
                    value={shown}
                    placeholder={t("field.empty")}
                    contentAfter={
          disabled ? undefined : (
                            <span className={styles.actions}>
                                <Button
                                    appearance="transparent"
                                    size="small"
                                    icon={<LookupSearchIcon />}
                                    aria-label={t("field.lookup.change", {
                                        label: attr.displayName,
                                    })}
                                    onClick={() => setChoosing(true)}
                                />
                                {lookup && (
                                    <Button
                                        appearance="transparent"
                                        size="small"
                                        icon={<DeleteIcon />}
                                        aria-label={t("field.lookup.clear", {
                                            label: attr.displayName,
                                        })}
                                        onClick={() => onChange(null)}
                                    />
                                )}
                            </span>
                        )
                    }
                />
            </Field>
            {!disabled && (
                // The search opens in a side panel (Fluent OverlayDrawer at the end), closed when a record is picked.
                <OverlayDrawer
                    position="end"
                    size="medium"
                    open={choosing}
                    onOpenChange={(_, d) => setChoosing(d.open)}
                >
                    <DrawerHeader>
                        <DrawerHeaderTitle
                            action={
                                <Button
                                    appearance="subtle"
                                    icon={<DismissIcon />}
                                    aria-label={t("common.close")}
                                    onClick={() => setChoosing(false)}
                                />
                            }
                        >
                            {attr.displayName}
                        </DrawerHeaderTitle>
                    </DrawerHeader>
                    <DrawerBody className={styles.body}>
                        {targets.length > 1 && (
                            <Field
                                label={t("field.lookup.table", {
                                    label: attr.displayName,
                                })}
                            >
                                <Select
                                    value={target}
                                    onChange={(_, d) => setTarget(d.value)}
                                >
                                    <option value="">{t("field.empty")}</option>
                                    {targets.map((x) => (
                                        <option key={x} value={x}>
                                            {x}
                                        </option>
                                    ))}
                                </Select>
                            </Field>
                        )}
                        {target && (
                            <TargetPicker
                                key={target}
                                attr={attr}
                                target={target}
                                nav={nav}
                                value={lookup}
                                onChange={pick}
                                preSearch={preSearch}
                            />
                        )}
                    </DrawerBody>
                </OverlayDrawer>
            )}
        </>
    );
}

export default function ServerSelector({ serverNames, selected, onChange }) {
  return (
    <select
      className="server-selector"
      value={selected ?? ""}
      onChange={(e) => onChange(e.target.value || null)}
    >
      <option value="">전체 서버</option>
      {serverNames.map((name) => (
        <option key={name} value={name}>
          {name}
        </option>
      ))}
    </select>
  );
}

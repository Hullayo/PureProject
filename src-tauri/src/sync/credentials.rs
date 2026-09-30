use keyring::Entry;

pub fn save_credential(service: &str, key: &str, value: &str) -> Result<(), Box<dyn std::error::Error>> {
    let entry = Entry::new(service, key)?;
    entry.set_password(value)?;
    Ok(())
}

pub fn load_credential(service: &str, key: &str) -> Result<String, Box<dyn std::error::Error>> {
    let entry = Entry::new(service, key)?;
    Ok(entry.get_password()?)
}

use std::cmp::Ordering;

#[derive(Eq, PartialEq)]
pub(crate) struct Version
{
    core: Vec<String>,
    prerelease: Vec<String>,
}

impl Version
{
    pub(crate) fn parse(value: &str) -> Option<Self>
    {
        let value = value.strip_prefix('v').unwrap_or(value);
        let mut build = value.split('+');
        let main = build.next()?;
        if let Some(metadata) = build.next()
            && (build.next().is_some() || !identifiers(metadata, false)) {
            return None;
        }
        let (core, prerelease) = match main.split_once('-') {
            Some((core, pre)) if identifiers(pre, true) => (core, pre.split('.').map(str::to_owned).collect()),
            Some(_) => return None,
            None => (main, Vec::new()),
        };
        let core: Vec<_> = core.split('.').map(str::to_owned).collect();
        if core.len() != 3 || !core.iter().all(|part| number(part) && (part.len() == 1 || !part.starts_with('0'))) {
            return None;
        }
        Some(Self { core, prerelease })
    }
}

fn number(value: &str) -> bool
{
    !value.is_empty() && value.bytes().all(|c| c.is_ascii_digit())
}

fn identifiers(value: &str, no_leading_zero: bool) -> bool
{
    value.split('.').all(|part| {
        !part.is_empty() && part.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-')
            && !(no_leading_zero && number(part) && part.len() > 1 && part.starts_with('0'))
    })
}

fn compare_number(left: &str, right: &str) -> Ordering
{
    left.len().cmp(&right.len()).then_with(|| left.cmp(right))
}

impl Ord for Version
{
    fn cmp(&self, other: &Self) -> Ordering
    {
        for (left, right) in self.core.iter().zip(&other.core) {
            let order = compare_number(left, right);
            if order != Ordering::Equal {
                return order;
            }
        }
        if self.prerelease.is_empty() || other.prerelease.is_empty() {
            return self.prerelease.is_empty().cmp(&other.prerelease.is_empty());
        }
        for (left, right) in self.prerelease.iter().zip(&other.prerelease) {
            let order = match (number(left), number(right)) {
                (true, true) => compare_number(left, right),
                (true, false) => Ordering::Less,
                (false, true) => Ordering::Greater,
                (false, false) => left.cmp(right),
            };
            if order != Ordering::Equal {
                return order;
            }
        }
        self.prerelease.len().cmp(&other.prerelease.len())
    }
}

impl PartialOrd for Version
{
    fn partial_cmp(&self, other: &Self) -> Option<Ordering>
    {
        Some(self.cmp(other))
    }
}
